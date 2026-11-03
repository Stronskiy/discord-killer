# Agent workflow

This project is coordinated by one lead agent (the CEO) for each task. The CEO owns the plan, assignments, integration and final report. This is a role in the active session, not an autonomous service or a GitHub identity. Follow the user's instructions and the available host tools and permissions.

## Working agreement

1. Inspect the current repository state and applicable instructions. Use Superpowers brainstorming for design work and writing-plans for multi-step implementation where available.
2. Keep local plans and private notes outside the public repository. The `docs/` directories are ignored and do not appear automatically in other worktrees. Give each agent the relevant approved brief or a verified local path, plus the base commit. Do not expose private notes in PRs or artifacts.
3. Delegate independent tasks when the user authorizes subagents and the environment supports them. Otherwise, carry out the roles sequentially and report the limitation. Do not claim a dispatch that did not occur.
4. Assign file ownership and use isolated branches or worktrees for concurrent writes. Integrate dependent changes in order. Each agent reports its changes, base and resulting commit or diff, checks run, and open risks.
5. The CEO checks integrated changes on the current commit, resolves conflicts, arranges an independent review when available, and reports evidence and unverified behavior. An agent review is not a GitHub approval by another person.

## Roles

- **CEO / integrator:** scope, plan, task assignment, integration, verification and PR.
- **Windows core:** timer logic, process identity and shutdown behavior.
- **Windows UI:** window, tray, keyboard access and visual design.
- **macOS:** native application and distribution-ready assets.
- **CI / release:** workflows, builds, release assets and checksums.
- **QA / reviewer:** independent requirements and code review, with test evidence.
- **Security / OSS:** permissions, sensitive data, dependency licenses and asset provenance.

Use roles only as needed. No agent should review its own implementation as an independent reviewer.

## Git and pull requests

- Base work on an up-to-date `main`; use short-lived `feat/`, `fix/`, `chore/`, or `release/` branches.
- Open a pull request to `main`; describe behavior, checks and results, risks, and unverified scenarios. Keep changes focused. Do not commit private planning documents, secrets, local build output, or unapproved third-party brand assets.
- Check the actual pull request head and CI status before merge. Merge, tagging, release publication and repository settings follow the user's instructions and applicable permissions. Never force-push `main` or move a published release tag.
- Review and test the actual merge commit before tagging. A GitHub Release is created from a tag after the release PR is merged; attach artifacts built from that tag or verified commit and publish checksums. Mark a release as preliminary when necessary validation is missing.
- Windows runner results do not establish interactive tray behavior, appearance or real Discord shutdown. Report those separately until checked in an interactive Windows session.

## Security and licensing

The repository currently uses Apache-2.0. Review third-party dependencies and asset provenance before adding them. The Discord-derived assets from the local prototype are not approved for publication merely because this repository is open source. The temporary `Resources/AppIcon.icns` ignore rule also excludes a future original icon at that path; after reviewing its provenance, use a different name or update the rule. Never commit credentials, signing keys or personal data. Ignoring files does not remove tracked files from Git history or prevent forced additions.
