# Preview known limitations

- Debug adapter packaging is not qualified on Intel macOS; upstream does not publish that archive.
- Debugger views, extension-process isolation, Build Configurator UI, signing/notarization, installers, and atomic updates are still under implementation.
- Native interaction automation runs under Linux/Xvfb. Windows and macOS receive build, test, and package checks but still need retained clean-image UI evidence for release qualification.
- Legacy/non-SDK projects, JavaScript/TypeScript intelligence, source-control UI, and remote development are outside preview scope.
- Uninstall retains settings and recovery data by design.
