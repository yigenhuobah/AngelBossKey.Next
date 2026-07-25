## Summary

Describe the user-visible behavior and why this change is needed.

## Verification

- [ ] `./eng/quality.ps1` passed.
- [ ] Regression tests cover the behavior change or the omission is explained.
- [ ] Any manual check avoided disruptive changes to a daily-use desktop.

## Security and Privacy

- [ ] No window titles, executable paths, arguments, recovery records, logs, tokens, or credentials were added to source control.
- [ ] Imported data, diagnostics, and pipe input remain validated at their boundaries.
- [ ] Elevated Broker changes cover authorization and invalid-request behavior.
