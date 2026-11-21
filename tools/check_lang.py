#!/usr/bin/env python3
"""Checks that every text the code uses is in assets/automaticclayformer/lang/en.json, with as many {n} places as the
code passes values. Run from the repository root; exits 1 on a problem. The build runs it first."""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREFIX = "automaticclayformer:"


def calls(text, name):
    """Every call name(...) in text, as its list of top-level arguments."""
    for m in re.finditer(r"(?<![\w.])" + re.escape(name) + r"\(", text):
        i = m.end()
        depth, args, cur, in_str, esc = 1, [], "", None, False
        while i < len(text) and depth > 0:
            c = text[i]
            if in_str:
                cur += c
                if esc:
                    esc = False
                elif c == "\\":
                    esc = True
                elif c == in_str:
                    in_str = None
            elif c in "\"'":
                in_str = c
                cur += c
            elif c in "([{":
                depth += 1
                cur += c
            elif c in ")]}":
                depth -= 1
                if depth > 0:
                    cur += c
            elif c == "," and depth == 1:
                args.append(cur.strip())
                cur = ""
            else:
                cur += c
            i += 1
        if cur.strip():
            args.append(cur.strip())
        yield text.count("\n", 0, m.start()) + 1, args


def literals(expr):
    return re.findall(r'"((?:[^"\\]|\\.)*)"', expr)


def needed(value):
    found = [int(n) for n in re.findall(r"\{(\d+)\}", value)]
    return max(found) + 1 if found else 0


def main():
    lang = json.load(open(os.path.join(ROOT, "assets", "automaticclayformer", "lang", "en.json"), encoding="utf-8"))
    problems, used = [], set()

    for folder, _, files in os.walk(os.path.join(ROOT, "src")):
        for f in files:
            if not f.endswith(".cs"):
                continue
            path = os.path.join(folder, f)
            rel = os.path.relpath(path, ROOT)
            text = open(path, encoding="utf-8").read()

            # Every "automaticclayformer:key" written anywhere must exist, except ids that aren't texts (const strings)
            for line_text in text.splitlines():
                if "const string" in line_text:
                    continue
                for key in re.findall(r'"' + re.escape(PREFIX) + r'([a-z0-9-]+)"', line_text):
                    used.add(key)
                    if key not in lang:
                        problems.append("%s: %s%s is not in en.json" % (rel, PREFIX, key))

            # T(lang, "key", args...) in DoctorBridge; Lang.Get("mod:key", args...); Lang.GetL(lang, "mod:key", args...)
            checks = [("T", 1, ""), ("Lang.Get", 0, PREFIX), ("Lang.GetL", 1, PREFIX)]
            for name, key_index, prefix in checks:
                for line, args in calls(text, name):
                    if len(args) <= key_index:
                        continue
                    passed = len(args) - key_index - 1
                    for lit in literals(args[key_index]):
                        if prefix and not lit.startswith(prefix):
                            continue
                        key = lit[len(prefix):] if prefix else lit
                        if not key:
                            continue   # a key built from parts, like PREFIX + key in T itself
                        if name == "T" and not re.fullmatch(r"[a-z0-9-]+", key):
                            continue
                        used.add(key)
                        if key not in lang:
                            problems.append("%s:%d: %s is not in en.json" % (rel, line, key))
                        elif needed(lang[key]) > passed:
                            problems.append("%s:%d: %s needs %d value(s), the code passes %d" % (rel, line, key, needed(lang[key]), passed))

    # Lang keys in the JSON file Vintage Doctor reads
    doctor_json = os.path.join(ROOT, "assets", "automaticclayformer", "config", "vintagedoctor.json")
    if os.path.exists(doctor_json):
        def walk(node):
            if isinstance(node, dict):
                for v in node.values():
                    yield from walk(v)
            elif isinstance(node, list):
                for v in node:
                    yield from walk(v)
            elif isinstance(node, str) and node.startswith(PREFIX) and " " not in node:
                yield node[len(PREFIX):]
        for key in walk(json.load(open(doctor_json, encoding="utf-8"))):
            used.add(key)
            if key not in lang:
                problems.append("assets/automaticclayformer/config/vintagedoctor.json: %s%s is not in en.json" % (PREFIX, key))

    print("%d texts in en.json, %d used by the code" % (len(lang), len(used)))
    for p in problems:
        print("::error title=Texts::" + p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
