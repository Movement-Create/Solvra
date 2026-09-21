cd "$WORK"
exp=$'region,units,revenue\nNorth,270,2565.00\nSouth,175,1925.00\nEast,60,735.00'
[ "$(tr -d '\r"' < out/revenue_by_region.csv | sed '/^$/d')" = "$exp" ] || fail "csv wrong: $(tr '\n' ' ' < out/revenue_by_region.csv)"
python3 -c 'import zipfile;z=zipfile.ZipFile("out/sales.xlsx");assert any("sheet" in n for n in z.namelist())' || fail "xlsx invalid"
head -c 4 out/summary.pdf | grep -q '%PDF' || fail "pdf invalid"
for t in csv_write spreadsheet_create doc_create; do tool_used $t || fail "$t not used"; done
