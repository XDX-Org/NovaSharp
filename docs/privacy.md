# Privacy

NovaSharp works on local files and does not send telemetry or crash reports in the preview build. Language servers, build tools, terminals, debug adapters, and extensions run locally. Network access occurs only when an explicitly invoked tool or approved extension performs it.

Logs redact absolute user/workspace paths, source text, and secret-shaped values. Settings, layout state, and dirty-buffer recovery remain in the platform's local application-data directory. Uninstalling the application does not delete this user data; users can remove it separately or use the reset/export controls when available.

Security or privacy reports should use GitHub's private vulnerability-reporting route for the NovaSharp repository. Ordinary support requests use the public issue tracker and must not include secrets or private source.
