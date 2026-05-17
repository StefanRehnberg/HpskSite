# HpskSite — pistol.nu

Umbraco-based platform for Swedish pistol-shooting clubs (precision, fältskytte, milsnabb,
duell, springskytte, magnumprecision, nationell helmatch). Powers https://pistol.nu — member
management, club administration, the Skyttetrappan training programme, competition
registration, start lists, results, payments via Swish, and instructor / certification
tracking.

## Licence

This project is licensed under the **Functional Source License, Version 1.1, Apache 2.0
Future License (FSL-1.1-Apache-2.0)**.

In short: you may read the source, study it, run it for your own internal use, contribute
back, and use it for non-commercial education or research. You **may not** use it to offer
a competing commercial product or service — anything that substitutes for this software or
provides substantially similar functionality. Two years after each release, that release
automatically converts to Apache-2.0.

This is a "source-available" licence, not an OSI "open source" licence. The intent is to
keep the project welcoming to collaborators and operators who want to *use* it, while
preventing it from being lifted wholesale into a competing platform. Contributions are very
much welcome — open an issue or a pull request.

See [LICENSE](LICENSE) for the full text. Copyright © 2024–2026 Stefan Rehnberg.

## What's in this repo — and what isn't

This repository contains the *engine*: the C# / Razor / JavaScript source code, SQL schema
migrations, generic CSS/JS, the Umbraco project structure, and the build configuration.

It deliberately does **not** contain the curated content that's specific to pistol.nu:

- The Swedish-pistol-club directory and SPSF references
- The hand-curated field-shooting target catalogue (Figurkatalog) and competition-type icons
- The in-site AI assistant's knowledge base and the internal architecture documentation
- Runtime user uploads (logos, profile pictures, event images), production secrets,
  marketing emails

A fresh clone gives you a working code base. The shape of the missing content and where to
get it is documented in [CONTENT.md](CONTENT.md).

## Live site

https://pistol.nu
