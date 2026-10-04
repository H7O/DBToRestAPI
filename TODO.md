# TODO

Follow-ups recommended after a change landed, so they are not lost between releases. Each item
names the note that explains the background.

## Open

- **Opt-in `<allowed_hosts>` for embedded HTTP calls.** The 1.6.0 escaping fix stops a *caller*
  from steering an `{http{ … }http}` block to another host, but a configuration author is still
  trusted to pick any host and there is no private-range block. An opt-in allow-list of
  destination hosts (global, overridable per endpoint) would close that. Background:
  [SECURITY_HARDENING_1.6.md](SECURITY_HARDENING_1.6.md), section 1, "Not done, and why".

- **Base64 upload content without padding is silently truncated.** A JSON upload whose
  `content_base64` lacks its trailing `=` padding is stored without its last bytes, and the
  request reports success. The streaming decoder in `WriteBase64ToTempFileStreaming` should
  either restore the padding or refuse the content with a 400. Found by the 1.7.3 review; it
  predates that release. Background: [ParametersBuilder.cs](DBToRestAPI/Services/ParametersBuilder.cs).

- **Two multipart file parts with the same name keep only the first one's content.** Metadata
  entries are matched to file parts by file name, so a second part with the same name is never
  read, and both entries store the first part's bytes. Duplicate names should be refused with a
  400, or matched by position. Found by the 1.7.3 review; it predates that release. Background:
  `ProcessFiles` in [ParametersBuilder.cs](DBToRestAPI/Services/ParametersBuilder.cs).

## Done

- **An invalid upload returned an empty 500, or was silently dropped.** Fixed in 1.7.3. Upload
  validation errors (file name, extension, size, count, content that is not base64, metadata
  shape) escaped parameter building, so a JSON upload got an empty 500. The multipart path
  swallowed them, along with malformed metadata and a body over the size limit, so the request
  ran on with every form field null and could report success. They are now a
  `RequestValidationException`, which both paths let through and which becomes a 400 that says
  why. A body over `max_payload_size_in_bytes` is a 413 for JSON and forms alike, a form over a
  `FormOptions` limit is a 400, and anything else that fails while reading parameters is a JSON
  500 with the error code. See `ParametersBuilder.GetParamsOrErrorAsync` in
  [ParametersBuilder.cs](DBToRestAPI/Services/ParametersBuilder.cs).

- **Keep a download's `relative_path` inside its store.** Shipped in 1.7.2. Before, the path a
  download query returned was joined to the store's `base_path` unchecked, so `..`, an absolute
  path, or on Windows a UNC or device path (`\\host\share\...`, `\??\UNC\...`) read any file the
  app's account could reach, on that machine or another one. Local and SFTP stores now refuse
  anything outside `base_path` with the same 404 as a missing file, and neither 404 echoes the
  resolved path any more. A local store without a `base_path` no longer falls back to the app's
  own folder. The upload side got the same check, and on Linux a file name containing `\` can
  no longer turn into a path outside the store. See
  [FileStorePath.cs](DBToRestAPI/Services/FileStorePath.cs) and
  [docs/topics/10-file-downloads.md](docs/topics/10-file-downloads.md).

- **SFTP uploads failed for every new file.** Fixed in Com.H.Net.Ssh 10.1.1, which 1.7.2
  references. In 10.1.0, `SFtpClient.Exists` and `ExistsAsync` threw `SftpPathNotFoundException`
  for a missing path instead of returning false, so the existence check that runs before each
  SFTP upload (with the default `overwrite_existing_files=false`) failed on every new file.

- **OpenAPI `429` annotation for rate-limited operations.** Shipped in 1.7.0 together with the
  rate-limiting feature itself. See [docs/topics/18-rate-limiting.md](docs/topics/18-rate-limiting.md)
  and [docs/topics/20-openapi.md](docs/topics/20-openapi.md).
