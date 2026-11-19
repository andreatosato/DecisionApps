# Decision Apps

Due applicazioni locali .NET 10 + Aspire con **Microsoft Agent Framework**
e **Microsoft-Decision-1.1**:

| App | Descrizione | Avvio dalla root |
|---|---|---|
| [ChatBot](SRC/ChatBot/README.md) | Classificazione delle domande dei cittadini e simulatore Excel | `dotnet run --project .\SRC\ChatBot\DecisionApps.AppHost` |
| [Pong](SRC/Pong/README.md) | Gioco 2D: umano contro una racchetta controllata da Decision | `dotnet run --project .\SRC\Pong\Pong.AppHost` |

Prerequisiti: .NET 10 SDK, deployment Decision 1.1 e autenticazione Foundry
tramite `az login` oppure API key nei user-secrets del rispettivo servizio.
Non servono Docker o database. Non vengono creati deployment o permessi Azure.
Le chiamate reali al modello possono essere fatturate.

La precedente applicazione e stata spostata integralmente in `SRC\ChatBot`,
inclusa la sua soluzione indipendente e la documentazione.
[DecisionApps.slnx](DecisionApps.slnx) raccoglie entrambe le app;
ogni app dispone anche di una soluzione e un AppHost autonomi.

## Verifica offline

```powershell
dotnet build .\DecisionApps.slnx
dotnet test .\SRC\ChatBot\DecisionApps.Tests
dotnet test .\SRC\Pong\Pong.Tests
node --test .\SRC\Pong\Pong.BrowserTests\agent.test.mjs .\SRC\Pong\Pong.BrowserTests\game.test.mjs .\SRC\Pong\Pong.BrowserTests\startup.test.mjs
```

I test non chiamano Azure. I dettagli su configurazione, comandi e limiti
sono nei README delle singole app. Prima di ricompilare su Windows, arrestare
gli AppHost eventualmente in esecuzione per evitare DLL bloccate.
