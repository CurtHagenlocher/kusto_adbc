# DataFusion plan fixtures

Substrait plans produced by DataFusion 55.0.0 (Python bindings) from the SQL in each `.sql` file.
`.pb` is the binary plan, `.json` a readable rendering, and `.kql` the expected translation
(or `ERROR: <message fragment>` when the plan should be rejected).

Regenerate the plans with `scripts/gen_datafusion_fixtures.py`; regenerate the `.kql` snapshots by running
the tests with `KUSTO_ADBC_UPDATE_SNAPSHOTS=1` and reviewing the diff.
