# DigiSEO Implementation Pack

This pack is designed to be used with an AI coding agent to implement DigiSEO inside the existing DigiStore solution.

## Files

- `01-Product-Overview.md` — product vision and scope
- `02-Technical-Specification.md` — detailed architecture and engineering specification
- `03-Master-AI-Coding-Prompt.md` — master prompt to give the coding AI
- `04-Phase-Prompts.md` — sequential prompts for implementation phases

## Recommended use

1. Give the AI the Master Prompt.
2. Give it the Technical Specification.
3. Run Prompt 00.
4. Review its repository assessment.
5. Execute prompts 01–24 sequentially.
6. Never ask the AI to generate the whole application in one response.
7. After each phase, require build + tests + security review.

## Important architecture decision

DigiSEO is a module/product inside DigiStore, not a second unrelated application.

The existing DigiStore authentication, ApplicationUser, Identity roles, localization and four-project architecture should be reused.

## External services

External providers are adapters:
- Google Search Console
- Bing Webmaster
- optional performance APIs
- optional AI providers

The core SEO engine must remain operational without paid third-party SEO databases or AI APIs.

## Source-of-truth principle

Raw facts come from:
- the website crawler
- the database
- user configuration
- optional verified provider data

AI explains and reasons over those facts. It does not manufacture facts.
