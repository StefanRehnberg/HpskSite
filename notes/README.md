# notes/ — lokala arbetsanteckningar

Allt i den här mappen är **ignorerat av git** (`/notes/*` i roten `.gitignore`). Bara den här
README:n är spårad, så mappen och dess syfte följer med en färsk klon.

Hit hör arbetsmaterial som uppstår under en session och som är värt att spara, men som inte är
projektdokumentation:

- **findings** — vad en genomgång faktiskt hittade, med `fil:rad` och bevis
- **testkörningar** — vad som kördes, vad som passerade, vad som återstod
- **skisser** — designresonemang innan något är byggt, och varför alternativen föll bort
- **handoffs** — var nästa session ska ta vid

Namnge med datum: `desk-shift-findings-2026-08-05.md`. Då går det att se hur färsk en uppgift är,
vilket är hela skillnaden mellan en användbar anteckning och en vilseledande.

## Vad som INTE hör hit

| | Var |
|---|---|
| Projektdokumentation (arkitektur, konventioner, systembeskrivningar) | `src/HpskSite/Documentation/` — spårad i git |
| Sanningskällan för öppet arbete | `backlog.md` i repo-roten |
| Användarvänd hjälptext som AI-chatten läser | `src/HpskSite/KnowledgeBase/docs/` |
| Beslut och varför-resonemang som ska överleva sessionen | Claudes minne, `.claude/projects/…/memory/` |

## Länka från backloggen

En anteckning som bär **öppna punkter** ska refereras från `backlog.md`. Annars blir den det enda
stället arbetet är nedtecknat, och ingen som läser backloggen får veta att det finns — vilket är
precis vad som hänt tidigare med fyra av filerna här.
