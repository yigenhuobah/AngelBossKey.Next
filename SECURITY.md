# Security Policy

## Supported code

Security fixes are made against the current `main` branch. Until public releases
exist, published version support has not been established.

## Reporting a vulnerability

When this repository is published on GitHub, report vulnerabilities through the
repository's private security-advisory channel. Do not disclose a suspected
vulnerability in a public issue, discussion, or pull request before a fix is
available.

Include the affected commit or build, Windows version, minimal reproduction
steps, expected and actual behavior, and the security impact. Redact window
titles, executable paths, launch arguments, recovery files, logs, and any
tokens before sharing supporting material.

## Security boundaries

The main process runs as the current user. The optional Elevated Broker is a
short-lived helper for explicit window query, hide, and restore operations. It
uses a current-user named pipe, a one-time token, fixed-time token comparison,
and validates both the requested command and target-window identity. It is not
a general privileged command runner.

Configuration import, recovery journals, and diagnostic reports are untrusted
or shareable-data boundaries. New code must validate imported data, avoid
logging sensitive window content, and keep secrets out of source control. The
rolling diagnostic log records exception types rather than exception messages
to avoid accidental disclosure through operating-system error text.
