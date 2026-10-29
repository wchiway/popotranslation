using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace popotranslation.Services;

/// <summary>
/// Checks the public <c>wchiway/popotranslation</c> GitHub repository for its latest stable release
/// and downloads the matching x64 installer into an isolated temporary directory.
/// The installer is never started. Network and release metadata are validated strictly, and the
/// downloaded bytes must match both the published size and the GitHub SHA-256 asset digest.
/// </summary>
internal sealed class ReleaseUpdateService
{
    private const string Owner = "wchiway";
    private const string Repository = "popotranslation";
    private const string ProductName = "PopoTranslation";
    // Current REST API version from the official release endpoints documentation (checked 2026-10-29):
    // https://docs.github.com/en/rest/releases/releases
    private const string GitHubApiVersion = "2026-03-10";
    private const string UserAgent = "PopoTranslation-Update/1.0";
    private const string Sha256Prefix = "sha256:";
    private const long MaxMetadataBytes = 2L * 1024 * 1024;
    private const long MaxInstallerBytes = 512L * 1024 * 1024;
    private const int MaxRedirects = 5;
    private const int StreamBufferSize = 81920;
    private const long ProgressReportInterval = 1024 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest", UriKind.Absolute);
    private static readonly Regex VersionPattern = new(
        @"\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)" +
        @"(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?" +
        @"(?:\+(?<build>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Lazy<HttpClient> SharedClient = new(CreateHttpClient, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly HttpClient _httpClient;

    public ReleaseUpdateService() : this(SharedClient.Value)
    {
    }

    /// <summary>
    /// Testing seam. The supplied client must use an infinite timeout and must not follow redirects
    /// automatically; this service validates every redirect target before requesting it.
    /// </summary>
    internal ReleaseUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Fetches the latest stable GitHub release and validates the tag, version, installer file name,
    /// GitHub URLs, file size, and SHA-256 asset digest.
    /// </summary>
    /// <param name="currentVersion">
    /// The caller's current version. It must be SemVer; prerelease suffixes take part in the comparison,
    /// while build metadata is ignored.
    /// </param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The validated latest stable release and whether it is newer than <paramref name="currentVersion"/>.</returns>
    /// <exception cref="ReleaseUpdateException">The request, response, or release metadata failed validation.</exception>
    public async Task<ReleaseUpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var current = ParseCurrentVersion(currentVersion);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CheckTimeout);
        try
        {
            ReleaseUpdateInfo release;
            using (var request = CreateLatestReleaseRequest())
            using (var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw CreateCheckStatusException(response.StatusCode);
                if (response.RequestMessage?.RequestUri is { } responseUri && !IsLatestReleaseUri(responseUri))
                    throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 返回了意外的重定向。");
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 返回的内容类型无效。");
                var payload = await ReadLimitedAsync(response.Content, MaxMetadataBytes, deadline.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
                release = ParseLatestRelease(document.RootElement);
            }

            if (!SemanticVersion.TryParse(release.Version, out var latest))
                throw new ReleaseUpdateException(ReleaseUpdateError.InvalidRelease, "GitHub 最新版本的版本号无效。");
            var isUpdateAvailable = SemanticVersion.Compare(latest, current) > 0;
            return new ReleaseUpdateCheckResult(currentVersion, isUpdateAvailable, release);
        }
        catch (JsonException ex)
        {
            throw new ReleaseUpdateException(
                ReleaseUpdateError.InvalidResponse, "GitHub 返回的版本信息不是有效的 JSON。", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseUpdateException(ReleaseUpdateError.TimedOut, "检查更新超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseUpdateException(
                ReleaseUpdateError.NetworkUnavailable, "无法连接 GitHub 检查更新，请检查网络。", ex);
        }
        catch (IOException ex)
        {
            throw new ReleaseUpdateException(
                ReleaseUpdateError.NetworkUnavailable, "读取 GitHub 更新信息失败，请检查网络。", ex);
        }
    }

    /// <summary>
    /// Streams the installer described by a previously validated result into a new, isolated
    /// temporary directory. The file is committed only after its exact size and SHA-256 digest match.
    /// The caller owns the returned file and is responsible for starting it after user confirmation.
    /// </summary>
    /// <param name="release">A release returned by <see cref="CheckForUpdateAsync"/>.</param>
    /// <param name="progress">Optional byte-level progress updates.</param>
    /// <param name="cancellationToken">Cancels the download and removes incomplete files.</param>
    /// <returns>The verified installer path. The installer is not executed.</returns>
    /// <exception cref="ReleaseUpdateException">The request, transfer, size, digest, or file write failed.</exception>
    public async Task<ReleaseDownloadResult> DownloadInstallerAsync(
        ReleaseUpdateInfo release,
        IProgress<ReleaseDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ValidateReleaseInfo(release);
        var expectedHash = Convert.FromHexString(release.InstallerSha256);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(DownloadTimeout);
        DirectoryInfo? directory = null;
        var completed = false;
        var lastReported = 0L;
        try
        {
            using (var response = await SendAssetAsync(release.InstallerUri, deadline.Token).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw CreateDownloadStatusException(response.StatusCode);
                if (response.Content.Headers.ContentLength is long declaredLength &&
                    declaredLength != release.InstallerSize)
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.SizeMismatch, "安装器大小与发布信息不一致。");

                directory = CreateDownloadDirectory();
                var finalPath = Path.Combine(directory.FullName, release.InstallerFileName);
                var partPath = finalPath + ".part";
                var total = 0L;
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await using var source = await response.Content
                        .ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                    await using (var file = new FileStream(
                        partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                        try
                        {
                            while (true)
                            {
                                var read = await source.ReadAsync(
                                    buffer.AsMemory(0, buffer.Length), deadline.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                total += read;
                                if (total > release.InstallerSize)
                                    throw new ReleaseUpdateException(
                                        ReleaseUpdateError.SizeMismatch, "下载内容超过发布信息中的文件大小。");
                                hash.AppendData(buffer, 0, read);
                                await file.WriteAsync(
                                    buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                                if (progress is not null &&
                                    (total - lastReported >= ProgressReportInterval || total == release.InstallerSize))
                                {
                                    progress.Report(new ReleaseDownloadProgress(total, release.InstallerSize));
                                    lastReported = total;
                                }
                            }
                            await file.FlushAsync(deadline.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }

                    if (total != release.InstallerSize)
                        throw new ReleaseUpdateException(
                            ReleaseUpdateError.SizeMismatch, "安装器大小与发布信息不一致。");
                    var actualHash = hash.GetHashAndReset();
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                        throw new ReleaseUpdateException(
                            ReleaseUpdateError.DigestMismatch, "安装器 SHA-256 校验失败。");
                }

                deadline.Token.ThrowIfCancellationRequested();
                File.Move(partPath, finalPath, overwrite: false);
                if (new FileInfo(finalPath).Length != release.InstallerSize)
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.SizeMismatch, "安装器大小与发布信息不一致。");
                if (progress is not null && lastReported != release.InstallerSize)
                    progress.Report(new ReleaseDownloadProgress(release.InstallerSize, release.InstallerSize));
                completed = true;
                return new ReleaseDownloadResult(release.Version, finalPath, release.InstallerSize, release.InstallerSha256);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseUpdateException(ReleaseUpdateError.TimedOut, "下载安装器超时，请稍后重试。");
        }
        catch (HttpRequestException ex)
        {
            throw new ReleaseUpdateException(
                ReleaseUpdateError.NetworkUnavailable, "无法连接 GitHub 下载安装器，请检查网络。", ex);
        }
        catch (IOException ex)
        {
            throw new ReleaseUpdateException(
                ReleaseUpdateError.DownloadFailed, "下载或保存安装器失败。", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ReleaseUpdateException(
                ReleaseUpdateError.DownloadFailed, "无法将安装器写入临时目录。", ex);
        }
        finally
        {
            if (!completed && directory is not null) TryDeleteDirectory(directory.FullName);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpRequestMessage CreateLatestReleaseRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", GitHubApiVersion);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    private static HttpRequestMessage CreateAssetRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    private async Task<HttpResponseMessage> SendAssetAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = CreateAssetRequest(current);
            var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                if (!IsAllowedAssetRedirectUri(current))
                {
                    response.Dispose();
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.InvalidInstallerUrl, "安装器下载地址不是预期的 GitHub 地址。");
                }
                return response;
            }

            try
            {
                var location = response.Headers.Location;
                if (location is null)
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.InvalidInstallerUrl, "安装器下载返回了无效的重定向。");
                if (redirectCount >= MaxRedirects)
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.InvalidInstallerUrl, "安装器下载重定向次数过多。");
                Uri next;
                try
                {
                    next = location.IsAbsoluteUri ? location : new Uri(current, location);
                }
                catch (UriFormatException)
                {
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.InvalidInstallerUrl, "安装器下载返回了无效的重定向地址。");
                }
                if (!IsAllowedAssetRedirectUri(next))
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.InvalidInstallerUrl, "安装器下载被重定向到了不受信任的地址。");
                current = next;
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private static ReleaseUpdateInfo ParseLatestRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 版本信息结构无效。");
        var tagName = GetRequiredString(root, "tag_name");
        if (GetRequiredBoolean(root, "draft") || GetRequiredBoolean(root, "prerelease"))
            throw new ReleaseUpdateException(ReleaseUpdateError.UnstableRelease, "GitHub 最新版本不是正式版。");
        if (tagName.Length is < 2 or > 129 || tagName[0] != 'v')
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidRelease, "GitHub 最新版本的标签无效。");
        var version = tagName[1..];
        if (!SemanticVersion.TryParse(version, out var parsed) ||
            parsed.Prerelease.Length != 0 || parsed.HasBuildMetadata)
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidRelease, "GitHub 最新版本不是正式版。");

        var releasePageUri = BuildReleasePageUri(tagName);
        if (!IsExactGitHubUri(GetRequiredString(root, "html_url"), releasePageUri))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 发布页面地址无效。");

        var installerFileName = BuildInstallerFileName(version);
        var assets = GetRequiredArray(root, "assets");
        JsonElement? match = null;
        var matches = 0;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object)
                throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 附件信息结构无效。");
            if (string.Equals(GetRequiredString(asset, "name"), installerFileName, StringComparison.Ordinal))
            {
                matches++;
                match = asset;
            }
        }
        if (matches != 1 || match is null)
            throw new ReleaseUpdateException(
                ReleaseUpdateError.InstallerAssetMissing,
                $"未找到唯一的 {installerFileName} 安装器附件。");

        var installer = match.Value;
        if (!string.Equals(GetRequiredString(installer, "state"), "uploaded", StringComparison.Ordinal))
            throw new ReleaseUpdateException(ReleaseUpdateError.InstallerAssetMissing, "安装器附件尚未上传完成。");
        if (!installer.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 安装器大小信息无效。");
        if (size <= 0 || size > MaxInstallerBytes)
            throw new ReleaseUpdateException(ReleaseUpdateError.InstallerSizeInvalid, "安装器大小超出允许范围。");
        if (!installer.TryGetProperty("digest", out var digestElement) ||
            digestElement.ValueKind != JsonValueKind.String ||
            !TryParseSha256Digest(digestElement.GetString(), out var sha256))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidDigest, "安装器缺少有效的 SHA-256 摘要。");

        var installerUri = BuildInstallerUri(tagName, installerFileName);
        if (!IsExactGitHubUri(GetRequiredString(installer, "browser_download_url"), installerUri))
            throw new ReleaseUpdateException(
                ReleaseUpdateError.InvalidInstallerUrl, "安装器下载地址不是预期的 GitHub 地址。");

        return new ReleaseUpdateInfo(
            version, tagName, releasePageUri, installerUri, installerFileName, size, sha256);
    }

    private static void ValidateReleaseInfo(ReleaseUpdateInfo release)
    {
        var version = release.Version;
        var tagName = release.TagName;
        var installerFileName = release.InstallerFileName;
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(installerFileName))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidRelease, "下载信息中的版本无效。");
        if (!SemanticVersion.TryParse(version, out var parsed) ||
            parsed.Prerelease.Length != 0 || parsed.HasBuildMetadata ||
            !string.Equals(tagName, "v" + version, StringComparison.Ordinal))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidRelease, "下载信息中的版本无效。");
        var expectedFileName = BuildInstallerFileName(version);
        if (!string.Equals(installerFileName, expectedFileName, StringComparison.Ordinal))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidInstallerName, "下载信息中的安装器文件名无效。");
        if (release.InstallerSize <= 0 || release.InstallerSize > MaxInstallerBytes)
            throw new ReleaseUpdateException(ReleaseUpdateError.InstallerSizeInvalid, "安装器大小超出允许范围。");
        if (!IsSha256Hex(release.InstallerSha256))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidDigest, "下载信息中的 SHA-256 摘要无效。");
        if (!IsExactGitHubUri(release.InstallerUri, BuildInstallerUri(tagName, expectedFileName)))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidInstallerUrl, "下载信息中的安装器地址无效。");
        if (!IsExactGitHubUri(release.ReleasePageUri, BuildReleasePageUri(tagName)))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "下载信息中的发布页面地址无效。");
    }

    private static SemanticVersion ParseCurrentVersion(string currentVersion)
    {
        if (!SemanticVersion.TryParse(currentVersion, out var current))
            throw new ReleaseUpdateException(
                ReleaseUpdateError.InvalidCurrentVersion, "当前版本号无效，必须是 SemVer 格式。");
        return current;
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        long limit,
        CancellationToken cancellationToken)
    {
        var declaredLength = content.Headers.ContentLength;
        if (declaredLength is long declared && declared > limit)
            throw new ReleaseUpdateException(ReleaseUpdateError.ResponseTooLarge, "GitHub 响应超过允许的大小。");
        var capacity = declaredLength is > 0 and <= MaxMetadataBytes ? (int)declaredLength.Value : 4096;
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(capacity);
        var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                if (memory.Length + read > limit)
                    throw new ReleaseUpdateException(
                        ReleaseUpdateError.ResponseTooLarge, "GitHub 响应超过允许的大小。");
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 版本信息缺少必要字段。");
        return value.GetString() ?? throw new ReleaseUpdateException(
            ReleaseUpdateError.InvalidResponse, "GitHub 版本信息包含空字段。");
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 版本信息缺少必要字段。");
        return value.GetBoolean();
    }

    private static JsonElement GetRequiredArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new ReleaseUpdateException(ReleaseUpdateError.InvalidResponse, "GitHub 版本信息缺少必要字段。");
        return value;
    }

    private static bool TryParseSha256Digest(string? text, out string normalized)
    {
        normalized = "";
        if (text is null || !text.StartsWith(Sha256Prefix, StringComparison.Ordinal) ||
            !IsSha256Hex(text.AsSpan(Sha256Prefix.Length)))
            return false;
        normalized = text[Sha256Prefix.Length..].ToLowerInvariant();
        return true;
    }

    private static bool IsSha256Hex(string? text) => text is not null && IsSha256Hex(text.AsSpan());

    private static bool IsSha256Hex(ReadOnlySpan<char> hex)
    {
        if (hex.Length != 64) return false;
        foreach (var character in hex)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F')))
                return false;
        }
        return true;
    }

    private static string BuildInstallerFileName(string version) =>
        $"{ProductName}.{version}.Setup.x64.exe";

    private static Uri BuildReleasePageUri(string tagName) =>
        new($"https://github.com/{Owner}/{Repository}/releases/tag/{tagName}", UriKind.Absolute);

    private static Uri BuildInstallerUri(string tagName, string installerFileName) =>
        new($"https://github.com/{Owner}/{Repository}/releases/download/{tagName}/{installerFileName}", UriKind.Absolute);

    private static bool IsLatestReleaseUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        string.Equals(uri.AbsoluteUri, LatestReleaseUri.AbsoluteUri, StringComparison.Ordinal);

    private static bool IsExactGitHubUri(string? text, Uri expected)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var actual)) return false;
        return IsExactGitHubUri(actual, expected);
    }

    private static bool IsExactGitHubUri(Uri? actual, Uri expected) =>
        actual is { IsAbsoluteUri: true } &&
        string.Equals(actual.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
        actual.IsDefaultPort &&
        string.IsNullOrEmpty(actual.UserInfo) &&
        string.IsNullOrEmpty(actual.Query) &&
        string.IsNullOrEmpty(actual.Fragment) &&
        string.Equals(actual.AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal);

    private static bool IsAllowedAssetRedirectUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        var host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static ReleaseUpdateException CreateCheckStatusException(HttpStatusCode statusCode)
    {
        var message = statusCode switch
        {
            HttpStatusCode.NotFound => "仓库没有可用的正式版本。",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                "GitHub 暂时拒绝访问，可能触发了速率限制，请稍后重试。",
            _ when IsRedirect(statusCode) => "GitHub 返回了意外的重定向。",
            _ => $"检查更新时 GitHub 返回了 HTTP {(int)statusCode}。"
        };
        return new ReleaseUpdateException(ReleaseUpdateError.HttpStatus, message, statusCode: statusCode);
    }

    private static ReleaseUpdateException CreateDownloadStatusException(HttpStatusCode statusCode)
    {
        var message = statusCode switch
        {
            HttpStatusCode.NotFound => "安装器文件不存在或已删除。",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
                "GitHub 暂时拒绝下载，请稍后重试。",
            _ => $"下载安装器时 GitHub 返回了 HTTP {(int)statusCode}。"
        };
        return new ReleaseUpdateException(ReleaseUpdateError.HttpStatus, message, statusCode: statusCode);
    }

    private static DirectoryInfo CreateDownloadDirectory()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), ProductName, "updates");
            Directory.CreateDirectory(root);
            return Directory.CreateDirectory(Path.Combine(root, "download-" + Guid.NewGuid().ToString("N")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ReleaseUpdateException(
                ReleaseUpdateError.DownloadFailed, "无法创建安装器临时目录。", ex);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* Best-effort cleanup. */ }
    }

    /// <summary>Minimal SemVer 2.0 parser and precedence comparer. Build metadata is ignored for ordering.</summary>
    private readonly struct SemanticVersion
    {
        private readonly string _major;
        private readonly string _minor;
        private readonly string _patch;
        private readonly string[] _prerelease;

        private SemanticVersion(string major, string minor, string patch, string[] prerelease, bool hasBuildMetadata)
        {
            _major = major;
            _minor = minor;
            _patch = patch;
            _prerelease = prerelease;
            HasBuildMetadata = hasBuildMetadata;
        }

        public string[] Prerelease => _prerelease ?? Array.Empty<string>();
        public bool HasBuildMetadata { get; }

        public static bool TryParse(string? text, out SemanticVersion version)
        {
            version = default;
            if (string.IsNullOrEmpty(text) || text.Length > 128) return false;
            var match = VersionPattern.Match(text);
            if (!match.Success) return false;

            var prereleaseText = match.Groups["prerelease"].Value;
            string[] prerelease;
            if (prereleaseText.Length == 0)
            {
                prerelease = Array.Empty<string>();
            }
            else
            {
                prerelease = prereleaseText.Split('.');
                foreach (var identifier in prerelease)
                {
                    if (identifier.Length == 0) return false;
                    var numeric = IsNumeric(identifier);
                    if (numeric && identifier.Length > 1 && identifier[0] == '0') return false;
                }
            }

            version = new SemanticVersion(
                match.Groups["major"].Value,
                match.Groups["minor"].Value,
                match.Groups["patch"].Value,
                prerelease,
                match.Groups["build"].Success);
            return true;
        }

        public static int Compare(SemanticVersion left, SemanticVersion right)
        {
            var result = CompareNumericIdentifier(left._major, right._major);
            if (result != 0) return result;
            result = CompareNumericIdentifier(left._minor, right._minor);
            if (result != 0) return result;
            result = CompareNumericIdentifier(left._patch, right._patch);
            if (result != 0) return result;
            return ComparePrerelease(left.Prerelease, right.Prerelease);
        }

        private static int ComparePrerelease(string[] left, string[] right)
        {
            if (left.Length == 0 && right.Length == 0) return 0;
            if (left.Length == 0) return 1;
            if (right.Length == 0) return -1;

            var count = Math.Min(left.Length, right.Length);
            for (var index = 0; index < count; index++)
            {
                var leftIdentifier = left[index];
                var rightIdentifier = right[index];
                var leftNumeric = IsNumeric(leftIdentifier);
                var rightNumeric = IsNumeric(rightIdentifier);
                int comparison;
                if (leftNumeric && rightNumeric)
                    comparison = CompareNumericIdentifier(leftIdentifier, rightIdentifier);
                else if (leftNumeric)
                    comparison = -1;
                else if (rightNumeric)
                    comparison = 1;
                else
                    comparison = string.CompareOrdinal(leftIdentifier, rightIdentifier);
                if (comparison != 0) return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }

        private static int CompareNumericIdentifier(string left, string right)
        {
            if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
            return string.CompareOrdinal(left, right);
        }

        private static bool IsNumeric(string value)
        {
            foreach (var character in value)
            {
                if (character is < '0' or > '9') return false;
            }
            return true;
        }
    }
}

/// <summary>The kind of release-update failure, suitable for localized UI messages.</summary>
internal enum ReleaseUpdateError
{
    InvalidCurrentVersion,
    InvalidRelease,
    UnstableRelease,
    InvalidResponse,
    ResponseTooLarge,
    HttpStatus,
    NetworkUnavailable,
    TimedOut,
    InstallerAssetMissing,
    InstallerSizeInvalid,
    InvalidInstallerName,
    InvalidInstallerUrl,
    InvalidDigest,
    DownloadFailed,
    SizeMismatch,
    DigestMismatch
}

/// <summary>A validated GitHub update failure. The message never contains response bodies or signed URLs.</summary>
internal sealed class ReleaseUpdateException : Exception
{
    public ReleaseUpdateException(
        ReleaseUpdateError error,
        string message,
        Exception? innerException = null,
        HttpStatusCode? statusCode = null)
        : base(message, innerException)
    {
        Error = error;
        StatusCode = statusCode;
    }

    public ReleaseUpdateError Error { get; }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>A stable GitHub release whose installer metadata passed validation.</summary>
/// <param name="Version">Stable three-part SemVer version without a <c>v</c> prefix.</param>
/// <param name="TagName">GitHub tag, exactly <c>v{Version}</c>.</param>
/// <param name="ReleasePageUri">Expected GitHub release page.</param>
/// <param name="InstallerUri">Expected GitHub installer download URL.</param>
/// <param name="InstallerFileName">Exactly <c>PopoTranslation.{Version}.Setup.x64.exe</c>.</param>
/// <param name="InstallerSize">Published size in bytes.</param>
/// <param name="InstallerSha256">Lowercase 64-character SHA-256 hex digest.</param>
internal sealed record ReleaseUpdateInfo(
    string Version,
    string TagName,
    Uri ReleasePageUri,
    Uri InstallerUri,
    string InstallerFileName,
    long InstallerSize,
    string InstallerSha256);

/// <summary>Result of a latest-release check.</summary>
/// <param name="CurrentVersion">The caller-supplied version used for comparison.</param>
/// <param name="IsUpdateAvailable">True when the stable release has higher SemVer precedence.</param>
/// <param name="Release">The validated latest stable release.</param>
internal sealed record ReleaseUpdateCheckResult(
    string CurrentVersion,
    bool IsUpdateAvailable,
    ReleaseUpdateInfo Release);

/// <summary>A verified installer downloaded into an isolated temporary directory. It has not been run.</summary>
internal sealed record ReleaseDownloadResult(
    string Version,
    string FilePath,
    long Size,
    string Sha256);

/// <summary>Download progress in bytes. <see cref="TotalBytes"/> is the validated installer size.</summary>
internal readonly record struct ReleaseDownloadProgress(long BytesReceived, long TotalBytes);
