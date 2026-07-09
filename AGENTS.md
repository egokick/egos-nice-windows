# Agent Workflow

Before starting work, take the user's prompt and use `$prompt-verifier` to verify the downstream work prompt.

Keep refining the prompt until `$prompt-verifier` returns `PASS`.

After the prompt passes, do the requested work.

When the work is complete, use `$work-verifier` to verify the result.

Only finish when `$work-verifier` returns `PASS`.
