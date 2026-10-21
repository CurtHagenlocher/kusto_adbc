"""Generate Substrait plan fixtures from SQL with DataFusion.

Usage: python gen_fixtures.py <output-dir>
Writes <name>.pb (binary plan), <name>.json (readable) and <name>.sql.
"""
import os, sys
import pyarrow as pa
import datafusion
from datafusion import SessionContext
from datafusion import substrait as ss
from substrait import plan_pb2
from google.protobuf import json_format

def ctx():
    c = SessionContext()
    # Column types match what the Kusto driver reports via GetTableSchema.
    orders = pa.table({
        "id": pa.array([1], pa.int64()), "customer_id": pa.array([1], pa.int64()),
        "name": pa.array(["a"]), "price": pa.array([1.0]), "qty": pa.array([1], pa.int32()),
        "ts": pa.array([0], pa.timestamp("us", tz="UTC")), "flag": pa.array([True]),
    })
    customers = pa.table({"id": pa.array([1], pa.int64()), "cname": pa.array(["x"]), "region": pa.array(["r"])})
    c.register_record_batches("orders", [orders.to_batches()])
    c.register_record_batches("customers", [customers.to_batches()])
    return c

QUERIES = {
    "filter_project": "SELECT name, price * 2 AS double_price FROM orders WHERE qty > 5",
    "aggregate_sort_limit": "SELECT name, sum(price) AS total FROM orders WHERE qty > 5 GROUP BY name ORDER BY total DESC LIMIT 10",
    "arithmetic": "SELECT id + 1 AS a, price * 2.5 AS b, qty / 2 AS c, id % 3 AS d, -price AS e FROM orders",
    "like_between": "SELECT id FROM orders WHERE name LIKE 'a%' AND price BETWEEN 1 AND 5",
    "in_list": "SELECT id FROM orders WHERE name IN ('a', 'b') OR id IN (1, 2, 3)",
    "case_when": "SELECT CASE WHEN qty > 1 THEN 'many' WHEN qty = 1 THEN 'one' ELSE 'none' END AS c FROM orders",
    "cast": "SELECT CAST(qty AS BIGINT) AS q, CAST(price AS VARCHAR) AS p FROM orders",
    "is_null": "SELECT id FROM orders WHERE name IS NULL OR price IS NOT NULL",
    "string_functions": "SELECT upper(name) AS u, lower(name) AS l, substr(name, 2, 3) AS s, replace(name, 'a', 'b') AS r FROM orders",
    "string_predicates": "SELECT id FROM orders WHERE starts_with(name, 'a') OR ends_with(name, 'z') OR strpos(name, 'q') > 0",
    "string_length": "SELECT length(name) AS n FROM orders",
    "aggregates": "SELECT count(*) AS n, count(name) AS nn, count(DISTINCT name) AS nd, avg(price) AS a, min(price) AS lo, max(id) AS hi FROM orders",
    "stddev": "SELECT stddev(price) AS s FROM orders",
    "inner_join": "SELECT o.id, c.cname FROM orders o JOIN customers c ON o.customer_id = c.id",
    "left_join_extra_condition": "SELECT o.id, c.cname FROM orders o LEFT JOIN customers c ON o.customer_id = c.id AND c.region = 'r'",
    "join_same_column_names": "SELECT o.id AS oid, c.id AS cid FROM orders o JOIN customers c ON o.customer_id = c.id",
    "exists_semi_join": "SELECT id FROM orders o WHERE EXISTS (SELECT 1 FROM customers c WHERE c.id = o.customer_id)",
    "not_in_anti_join": "SELECT id FROM orders o WHERE customer_id NOT IN (SELECT id FROM customers)",
    "union_all": "SELECT name FROM orders UNION ALL SELECT cname FROM customers",
    "union_distinct": "SELECT name FROM orders UNION SELECT cname FROM customers",
    "distinct": "SELECT DISTINCT name FROM orders",
    "order_limit_offset": "SELECT id, name FROM orders ORDER BY price ASC, name DESC LIMIT 5 OFFSET 10",
    "timestamp_literal": "SELECT id FROM orders WHERE ts > TIMESTAMP '2024-01-01 00:00:00+00:00'",
    "date_part": "SELECT extract(year FROM ts) AS y FROM orders",
    "not_and_not_equal": "SELECT id FROM orders WHERE NOT flag AND price <> 3",
    "filtered_aggregate": "SELECT sum(price) FILTER (WHERE qty > 1) AS s FROM orders",
    "rollup": "SELECT name, count(*) AS n FROM orders GROUP BY ROLLUP(name)",
    "math": "SELECT abs(price) AS a, round(price, 2) AS r, ceil(price) AS c, floor(price) AS f, sqrt(price) AS s, power(price, 2.0) AS p FROM orders",
    "concat": "SELECT concat(name, 'x') AS c FROM orders",
    "cross_join": "SELECT o.id, c.cname FROM orders o, customers c",
    "group_by_expression": "SELECT qty % 2 AS parity, count(*) AS n FROM orders GROUP BY qty % 2",
    "nullif_coalesce": "SELECT nullif(name, 'a') AS n, coalesce(name, 'none') AS c FROM orders",
    "having": "SELECT customer_id, sum(price) AS total FROM orders GROUP BY customer_id HAVING sum(price) > 100",
    "sort_by_expression": "SELECT id FROM orders ORDER BY price * qty DESC",
    "multi_join": "SELECT o.id, c.cname, c2.region FROM orders o JOIN customers c ON o.customer_id = c.id JOIN customers c2 ON c.id = c2.id",
}

def main(out):
    os.makedirs(out, exist_ok=True)
    c = ctx()
    for name, sql in QUERIES.items():
        try:
            b = ss.Serde.serialize_bytes(sql, c)
        except Exception as e:
            print(f"{name}: PRODUCER ERROR {str(e).splitlines()[0][:150]}")
            continue
        p = plan_pb2.Plan(); p.ParseFromString(b)
        open(os.path.join(out, name + ".pb"), "wb").write(b)
        open(os.path.join(out, name + ".json"), "w", encoding="utf-8").write(json_format.MessageToJson(p, indent=1))
        open(os.path.join(out, name + ".sql"), "w", encoding="utf-8").write(sql + "\n")
        print(f"{name}: {len(b)} bytes")
    open(os.path.join(out, "README.md"), "w", encoding="utf-8").write(
        "# DataFusion plan fixtures\n\n"
        f"Substrait plans produced by DataFusion {datafusion.__version__} (Python bindings) from the SQL in each `.sql` file.\n"
        "`.pb` is the binary plan, `.json` a readable rendering, and `.kql` the expected translation\n"
        "(or `ERROR: <message fragment>` when the plan should be rejected).\n\n"
        "Regenerate the plans with `scripts/gen_datafusion_fixtures.py`; regenerate the `.kql` snapshots by running\n"
        "the tests with `KUSTO_ADBC_UPDATE_SNAPSHOTS=1` and reviewing the diff.\n")

if __name__ == "__main__":
    main(sys.argv[1])
