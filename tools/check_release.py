#!/usr/bin/env python3
"""Sanity checks on what gets published. Run from the repository root; exits 1 on a problem.

  python3 tools/check_release.py            modinfo.json: only keys the game knows (the Mod DB rejects a release
                                            with an "unexpected property"), demonic69 as the first author, a version
  python3 tools/check_release.py --zip      also the zip in release/: named after that version, with everything in it
"""
import json
import os
import re
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# The keys Vintage Story's ModInfo reads (Common/API/ModInfo.cs), compared without case like the game does
KNOWN_KEYS = {
    "type", "name", "modid", "version", "networkversion", "texturesize", "iconpath", "backgroundpaths", "description",
    "website", "authors", "contributors", "side", "requiredonclient", "requiredonserver", "dependencies",
}

IN_ZIP = [
    "modinfo.json",
    "modicon.png",
    "AutomaticClayFormer.dll",
    "assets/automaticclayformer/lang/en.json",
    "assets/automaticclayformer/config/vintagedoctor.json",
]


def main():
    problems = []
    info = json.load(open(os.path.join(ROOT, "modinfo.json"), encoding="utf-8"))

    for key in info:
        if key.lower() not in KNOWN_KEYS:
            problems.append('modinfo.json: "%s" isn\'t a key the game knows, and the Mod DB rejects the release for it '
                            "(a Mod DB page number goes in moddb.json instead)" % key)

    authors = info.get("authors") or []
    if not authors or authors[0] != "demonic69":
        problems.append('modinfo.json: the first author must be "demonic69", it is %r' % (authors[0] if authors else None))

    version = info.get("version", "")
    if not re.fullmatch(r"\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?", version):
        problems.append("modinfo.json: version %r isn't like 1.2.3" % version)

    if info.get("modid") != "automaticclayformer":
        problems.append("modinfo.json: modid is %r" % info.get("modid"))

    notes = open(os.path.join(ROOT, "RELEASE_NOTES.md"), encoding="utf-8").read()
    if "Automatic Clay Former " + version not in notes.split("\n", 1)[0]:
        problems.append("RELEASE_NOTES.md doesn't start with Automatic Clay Former %s (it becomes the release's notes)" % version)

    if "--zip" in sys.argv:
        folder = os.path.join(ROOT, "release")
        zips = sorted(f for f in os.listdir(folder) if f.endswith(".zip")) if os.path.isdir(folder) else []
        expected = "automaticclayformer_%s.zip" % version
        if zips != [expected]:
            problems.append("release/ has %s, not just %s" % (zips or "no zip", expected))
        else:
            with zipfile.ZipFile(os.path.join(folder, expected)) as z:
                names = set(n.replace("\\", "/") for n in z.namelist())
                for need in IN_ZIP:
                    if need not in names:
                        problems.append("%s has no %s" % (expected, need))
                if "modinfo.json" in names and json.loads(z.read("modinfo.json").decode("utf-8-sig")) != info:
                    problems.append("%s: its modinfo.json isn't the repository's" % expected)
                print("%s: %d files" % (expected, len(names)))

    print("version %s, authors %s" % (version, ", ".join(authors)))
    for p in problems:
        print("::error title=Release check::" + p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
