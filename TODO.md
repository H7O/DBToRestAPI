# TODO

Follow-ups recommended after a change landed, so they are not lost between releases. Each item
names the note that explains the background.

## Open

- **Opt-in `<allowed_hosts>` for embedded HTTP calls.** The 1.6.0 escaping fix stops a *caller*
  from steering an `{http{ … }http}` block to another host, but a configuration author is still
  trusted to pick any host and there is no private-range block. An opt-in allow-list of
  destination hosts (global, overridable per endpoint) would close that. Background:
  [SECURITY_HARDENING_1.6.md](SECURITY_HARDENING_1.6.md), section 1, "Not done, and why".

## Done

- **OpenAPI `429` annotation for rate-limited operations.** Shipped in 1.7.0 together with the
  rate-limiting feature itself. See [docs/topics/18-rate-limiting.md](docs/topics/18-rate-limiting.md)
  and [docs/topics/20-openapi.md](docs/topics/20-openapi.md).
