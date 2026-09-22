# Issue tracker: GitHub

Issues and specs live in `Ne0EX/translator-window` on GitHub.
Use the `gh` CLI with `--repo Ne0EX/translator-window` on issue commands;
this workspace currently has no Git remote.

## Operations

- Publish: `gh issue create --repo Ne0EX/translator-window --title "..." --body-file <file>`
- Fetch: `gh issue view <number> --repo Ne0EX/translator-window --comments`
- List: `gh issue list --repo Ne0EX/translator-window --state open --json number,title,body,labels`
- Comment: `gh issue comment <number> --repo Ne0EX/translator-window --body-file <file>`
- Label: `gh issue edit <number> --repo Ne0EX/translator-window --add-label "<label>"` (or `--remove-label`)
- Close: `gh issue close <number> --repo Ne0EX/translator-window`

Use `triage-labels.md` for label names. For multiline bodies, write
the exact text to a temporary file and pass `--body-file`.

## Pull requests as a triage surface

PRs as a request surface: no.
