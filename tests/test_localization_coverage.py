"""Every Chinese UI literal must have an English mapping or an explicit exception.

Scanned: Views/*.cs, Services/*.cs, src/PopoTranslation.Windows/**/*.cs.
Mapping source: Views/UiLocalization.cs. Exceptions: tests/localization_exceptions.txt.
"""

from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
DICTIONARY = ROOT / "Views" / "UiLocalization.cs"
EXCEPTIONS = ROOT / "tests" / "localization_exceptions.txt"
SCAN_GLOBS = ("Views/*.cs", "Services/*.cs", "src/PopoTranslation.Windows/**/*.cs")

KEY = re.compile(r'\["((?:[^"\\]|\\.)+)"\]\s*=')
# Plain literals only; interpolated ($"...") and raw ("""...""") strings are skipped by the lookbehind and the regex shape.
LITERAL = re.compile(r'(?<![$@"])"((?:[^"\\\n]|\\.)+)"(?!")')
CJK = re.compile(r"[\u3400-\u9fff]")


def strip_line_comment(line):
    in_string = False
    i = 0
    while i < len(line) - 1:
        if line[i] == '"' and (i == 0 or line[i - 1] != "\\"):
            in_string = not in_string
        elif not in_string and line[i : i + 2] == "//":
            return line[:i]
        i += 1
    return line


def chinese_literals(path):
    found = set()
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        for match in LITERAL.finditer(strip_line_comment(line)):
            text = match.group(1).encode().decode("unicode_escape", errors="ignore") if "\\u" in match.group(1) else match.group(1)
            if CJK.search(text):
                found.add(text.strip())
    return found


def load_exceptions():
    if not EXCEPTIONS.exists():
        return set()
    return {line.strip() for line in EXCEPTIONS.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.startswith("#")}


class LocalizationCoverageTests(unittest.TestCase):
    def setUp(self):
        self.keys = set(KEY.findall(DICTIONARY.read_text(encoding="utf-8-sig")))
        self.exceptions = load_exceptions()
        self.literals = set()
        for pattern in SCAN_GLOBS:
            for path in ROOT.glob(pattern):
                if path.resolve() != DICTIONARY.resolve():
                    self.literals |= chinese_literals(path)

    def test_dictionary_has_entries(self):
        self.assertGreater(len(self.keys), 100)

    def test_every_chinese_literal_is_localized_or_excepted(self):
        missing = sorted(self.literals - self.keys - self.exceptions)
        self.assertEqual([], missing, "\n未本地化的文案（加入 UiLocalization.English 或 tests/localization_exceptions.txt）：\n" + "\n".join(missing))

    def test_exceptions_are_still_in_use(self):
        stale = sorted(self.exceptions - self.literals)
        self.assertEqual([], stale, "\n已不存在于源码的例外项，请从 localization_exceptions.txt 删除：\n" + "\n".join(stale))

    def test_exceptions_do_not_shadow_dictionary_keys(self):
        self.assertEqual([], sorted(self.exceptions & self.keys))


if __name__ == "__main__":
    unittest.main()
