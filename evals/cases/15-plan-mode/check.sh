cd "$WORK"
git diff --quiet || fail "plan mode modified files"
grep -q "15" "$OUT" || fail "no plan described"
grep -qiE "plan mode|read-only|could not (apply|edit)|cannot (apply|edit)|not able to (apply|edit)|would change|proposed" "$OUT" || note "plan text did not mention read-only mode"
