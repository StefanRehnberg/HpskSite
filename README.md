# HpskSite — pistol.nu

Umbraco-based platform for Swedish pistol-shooting clubs (precision, fältskytte, milsnabb,
duell, springskytte, magnumprecision, nationell helmatch). Powers https://pistol.nu — member
management, club administration, the Skyttetrappan training programme, competition
registration, start lists, results, payments via Swish, and instructor / certification
tracking.

## Licence

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**.

In short: you are free to use, modify, and redistribute the code, but if you run a modified
version as a network service (which is the typical use here), **your modifications must be
published under the same licence**. See [LICENSE](LICENSE) for the full text.

Copyright © 2024–2026 Stefan Rehnberg.

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
