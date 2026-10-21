"""Differential test: runs each DataFusion fixture's SQL in DataFusion, and its translated KQL
(the reviewed .kql snapshot) on a Kusto cluster, over identical data, and compares the results.

Usage:
    python scripts/differential_test.py test/KustoAdbc.Tests/Fixtures/DataFusion

Environment:
    KUSTO_TOKEN_FILE   file containing a bearer token for the cluster (required; never printed)
    KUSTO_CLUSTER      cluster URL (default https://help.kusto.windows.net)
    KUSTO_DATABASE     database to run in (default Samples; the queries only use datatable() data)

Kusto strings can't be null, so '' and null compare equal. Rows are compared as multisets
unless the SQL has ORDER BY. Requires: pip install datafusion pyarrow
"""
import sys, os, json, math, datetime, urllib.request, urllib.error
import pyarrow as pa
from datafusion import SessionContext

FIX = sys.argv[1]
TOKEN = open(os.environ["KUSTO_TOKEN_FILE"], encoding="utf-8").read().strip()
CLUSTER = os.environ.get("KUSTO_CLUSTER", "https://help.kusto.windows.net").rstrip("/")
DATABASE = os.environ.get("KUSTO_DATABASE", "Samples")
UTC = datetime.timezone.utc
def ts(s): return datetime.datetime.fromisoformat(s).replace(tzinfo=UTC)

# Edge cases: null numeric keys/values on both sides, duplicate keys, negatives, NaN-free reals.
orders = {
    "id":          [1, 2, 3, 4, 5, 6, 7],
    "customer_id": [10, 10, 20, None, 30, None, 40],
    "name":        ["apple", "banana", "Avocado", "cherry", "apricot", "date", "a.b"],
    "price":       [3.0, None, 7.5, 2.25, 3.0, -1.5, 100.0],
    "qty":         [6, 1, None, 9, -3, 2, 7],
    "ts":          [ts("2023-12-31T23:59:59"), ts("2024-01-01T00:00:00"), ts("2024-03-15T12:30:00.123456"), None,
                    ts("2024-06-01T00:00:00"), ts("2025-01-01T00:00:00"), ts("2022-07-04T08:00:00")],
    "flag":        [True, False, None, True, False, True, None],
}
customers = {"id": [10, 20, None, 50], "cname": ["x", "y", "z", "w"], "region": ["r", "s", "r", "r"]}
schemas = {
    "orders": [("id", pa.int64(), "long"), ("customer_id", pa.int64(), "long"), ("name", pa.string(), "string"),
               ("price", pa.float64(), "real"), ("qty", pa.int32(), "int"), ("ts", pa.timestamp("us", tz="UTC"), "datetime"),
               ("flag", pa.bool_(), "bool")],
    "customers": [("id", pa.int64(), "long"), ("cname", pa.string(), "string"), ("region", pa.string(), "string")],
}
data = {"orders": orders, "customers": customers}

def kql_literal(v, kt):
    if v is None: return f"{kt}(null)"
    if kt == "string": return json.dumps(v)
    if kt == "bool": return "true" if v else "false"
    if kt == "datetime": return f"datetime({v.strftime('%Y-%m-%dT%H:%M:%S.%f')}Z)"
    if kt == "real": return f"real({v!r})"
    return f"{kt}({v})"

def datatable(name):
    cols = schemas[name]
    rows = []
    for i in range(len(data[name][cols[0][0]])):
        rows.extend(kql_literal(data[name][c][i], kt) for c, _, kt in cols)
    return f"let {name} = datatable({', '.join(f'{c}:{kt}' for c, _, kt in cols)})[{', '.join(rows)}];"

def ctx():
    c = SessionContext()
    for name, cols in schemas.items():
        t = pa.table({col: pa.array(data[name][col], at) for col, at, _ in cols})
        c.register_record_batches(name, [t.to_batches()])
    return c

def norm(v):
    # Kusto strings can't be null; a missing string is '' (the driver's documented policy).
    if v is None or v == "": return None
    if isinstance(v, bool): return v
    if isinstance(v, datetime.datetime):
        return v.astimezone(UTC).strftime("%Y-%m-%dT%H:%M:%S.%f")
    if isinstance(v, str) and len(v) >= 20 and v[4] == "-" and v[10] == "T":
        d = datetime.datetime.fromisoformat(v.replace("Z", "+00:00"))
        return d.astimezone(UTC).strftime("%Y-%m-%dT%H:%M:%S.%f")
    if isinstance(v, (int, float)):
        f = float(v)
        if math.isnan(f): return "NaN"
        return round(f, 9)
    if v == "NaN": return "NaN"
    return v

def run_kql(q):
    body = json.dumps({"db": DATABASE, "csl": q}).encode()
    req = urllib.request.Request(CLUSTER + "/v1/rest/query", data=body,
        headers={"Authorization": "Bearer " + TOKEN, "Content-Type": "application/json"})
    try:
        res = json.load(urllib.request.urlopen(req, timeout=60))
    except urllib.error.HTTPError as e:
        msg = e.read().decode(errors="replace")
        try: msg = json.loads(msg)["error"]["@message"]
        except Exception: pass
        raise RuntimeError(msg[:300])
    t = res["Tables"][0]
    return [c["ColumnName"] for c in t["Columns"]], [tuple(norm(v) for v in r) for r in t["Rows"]]

prefix = datatable("orders") + "\n" + datatable("customers") + "\n"
c = ctx()
ok = bad = skipped = 0
for name in sorted(f[:-4] for f in os.listdir(FIX) if f.endswith(".kql")):
    kql = open(os.path.join(FIX, name + ".kql"), encoding="utf-8").read().strip()
    sql = open(os.path.join(FIX, name + ".sql"), encoding="utf-8").read().strip()
    if kql.startswith("ERROR"):
        skipped += 1; continue
    try:
        df = c.sql(sql).to_arrow_table()
    except Exception as e:
        skipped += 1; print(f"SKIP {name}: DataFusion itself failed: {str(e).splitlines()[0][:120]}"); continue
    expected = [tuple(norm(v) for v in row.values()) for row in df.to_pylist()]
    try:
        cols, actual = run_kql(prefix + kql)
    except RuntimeError as e:
        bad += 1; print(f"FAIL {name}: KQL error {e}"); continue
    ordered = "ORDER BY" in sql.upper()
    exp_cmp = expected if ordered else sorted(expected, key=repr)
    act_cmp = actual if ordered else sorted(actual, key=repr)
    if cols != df.column_names or exp_cmp != act_cmp:
        bad += 1
        print(f"FAIL {name}: {sql}\n   columns  df={df.column_names} kusto={cols}\n   expected {exp_cmp}\n   actual   {act_cmp}")
    else:
        ok += 1
print(f"\nmatched {ok}, mismatched {bad}, rejected-by-design {skipped}")
