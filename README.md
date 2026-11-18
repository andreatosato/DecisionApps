# Decision Lab

Demo locale .NET 10 + Aspire per classificare le domande dei cittadini di Roma
Capitale con **Microsoft-Decision-1.1** e **Microsoft Agent Framework**.

## Avvio

Prerequisiti: .NET 10 SDK e autenticazione verso il deployment Foundry.
Non occorrono Docker, database o agenti operativi.

```powershell
az login
dotnet run --project .\DecisionApps.AppHost
```

Aprire `webfrontend` dalla dashboard Aspire. Endpoint e deployment sono gia
configurati in [appsettings.json](DecisionApps.ApiService/appsettings.json):

- Endpoint: `https://agenteinformativo.services.ai.azure.com/providers/microsoft/v1/systemone`
- Deployment: `Decision-1.1` (nome esatto del deployment, non necessariamente nome del modello).

L'API usa `DefaultAzureCredential` con lo scope
`https://cognitiveservices.azure.com/.default`. L'identita deve avere il ruolo
di inferenza appropriato, ad esempio **Cognitive Services User**, sulla risorsa.
Il progetto non crea risorse Azure e non modifica permessi.

In alternativa, configurare una API key **solo nei user-secrets dell'API**:

```powershell
dotnet user-secrets set "Decision:ApiKey" "<API-KEY>" --project .\DecisionApps.ApiService
```

Sono supportate anche le variabili `Decision__Endpoint`,
`Decision__Deployment` e `Decision__ApiKey`. Non inserire chiavi nel repository.
In caso di errore Azure CLI `Decryption failed`, ripristinare l'accesso con
`az login` nel proprio terminale o usare i user-secrets.

## Utilizzo

1. Caricare un `.xlsx` con una colonna `DOMANDE` o `DOMANDA`, senza distinzione
   di maiuscole e spazi ai bordi. L'intestazione deve trovarsi nelle prime
   30 righe usate. I fogli senza questa intestazione sono ignorati.
2. Scegliere il foglio e selezionare singole righe oppure tutto il foglio.
   **Seleziona tutte le domande** seleziona l'intero Excel, su tutti i fogli
   e tutte le pagine; **Deseleziona tutto** azzera la selezione globale.
   Le selezioni si conservano cambiando foglio; il pulsante mostra il totale
   delle richieste che verranno inviate.
3. Avviare il test. Le domande sono inviate **in sequenza**, con una richiesta
   Decision per domanda. E anche possibile provare una domanda manuale.
4. Confrontare i cinque punteggi, il destinatario scelto e le probabilita delle
   due azioni (sezione aperta per impostazione predefinita), quindi verificare la corrispondenza con l'agente atteso
   dall'ambito Excel. Esportare risultati ed errori in JSON prima di ricaricare la pagina.
5. In **Filtra risultati**, sopra l'elenco, scegliere **Tutte**, **Da rivedere**
   oppure **Non corrisponde all'ambito**. I conteggi e il filtro riguardano il
   foglio corrente e i test singoli; il filtro viene applicato prima della
   paginazione e cambiare filtro riporta alla prima pagina.
   **Da rivedere** usa il flag di confidenza del risultato; il filtro di ambito
   include solo differenze effettive fra agente atteso e selezionato, escludendo
   righe in attesa, errori e risultati non confrontabili.
   I riepiloghi globali, l'export e la selezione non vengono modificati:
   le righe nascoste restano eventualmente selezionate e **Seleziona foglio**
   continua a includere tutto il foglio. **Mostra tutte** rimuove un filtro
   senza corrispondenze. Un nuovo import ripristina **Tutte**.

L'importazione legge tutti i fogli compatibili, conserva foglio e riga originali,
salta celle vuote e non elimina i duplicati. Supporta quindi anche workbook
con copertina, statistiche e piu insiemi di test, come il file allegato.
Limiti: 10 MB caricati, 50 MB decompressi, 1024 voci ZIP, 10000 domande,
6000 caratteri per domanda. Formule nella colonna delle domande non ammesse.
Il file originale non e incluso nel repository.

## Token e tempo di esecuzione

Ogni classificazione mostra token di input, output e totali restituiti da
Decision in `usage.input_tokens` e `usage.output_tokens`, oltre alla durata
della sola chiamata HTTP al modello.
Questi dati sono inclusi nel JSON del risultato (`usage` ed
`elapsedMilliseconds`). Non si stimano token dal testo o dai caratteri.

`elapsedMilliseconds` misura dal momento immediatamente precedente a
`HttpClient.SendAsync` fino alla ricezione completa del corpo HTTP.
Sono esclusi acquisizione credenziali, preparazione del prompt, deserializzazione,
validazione delle probabilita, API e UI. **Rete e gateway sono inclusi**:
non si tratta del tempo puro d'inferenza interno del provider.

Il pannello **Ultima esecuzione / Esecuzione in corso** riporta i token
accumulati per le chiamate riuscite e il **Tempo totale modello**, somma delle
durate HTTP disponibili, non un cronometro del batch. Il tempo si aggiorna
solo quando termina una chiamata, includendo anche quelle fallite se misurate;
in assenza di misure mostra `--`, non uno zero inventato. Le chiamate annullate
senza risposta ricevuta non entrano nella somma. Non sono incluse attese tra
le domande. I contatori ripartono a ogni nuovo test, anche singolo.
I risultati precedenti restano consultabili, ma non entrano nei consumi
dell'ultima esecuzione. Rianalizzare una domanda comporta una nuova chiamata
e il relativo consumo, non una deduplicazione.
Gli errori dopo l'avvio della chiamata includono `modelElapsedMilliseconds`
nel Problem Details. L'export mantiene la lista di righe e aggiunge lo stesso
campo a ogni riga (anche errore, oppure null se non misurato);
`result.elapsedMilliseconds` resta disponibile per i risultati riusciti,
con la semantica HTTP-modello descritta sopra.

Se il servizio non restituisce `usage`, la classificazione rimane valida ma
la UI segnala che il consumo non e disponibile, senza inventare uno zero.
Il pannello indica quante risposte hanno un conteggio e quante ne sono prive:
con dati parziali i totali comprendono solo quelli comunicati. Errori e
richieste annullate possono aver consumato token non rendicontati.
Non si tratta quindi di una fattura o di un totale economico in euro.

## OpenTelemetry e GenAI

Aspire riceve **tracce, metriche e log via OTLP** tramite i Service Defaults.
L'API registra esplicitamente le sorgenti e i meter `DecisionApps.Agents`
e `DecisionApps.Models`. L'agente usa il middleware OpenTelemetry nativo di
Microsoft Agent Framework; il protocollo proprietario `systemone` ha uno span
client dedicato, senza fingere una richiesta Chat Completions.

Nella dashboard Aspire aprire **Traces**, eseguire una domanda dal simulatore
e aprire la traccia `simulator.run`. La gerarchia attraversa i due servizi:

```text
simulator.run (webfrontend)
  HTTP POST /api/ratings (client)
    POST /api/ratings (server, apiservice)
      invoke_agent DecisionRoutingAgent (Agent Framework)
        classify Decision-1.1 (client GenAI)
          HTTP POST .../providers/microsoft/v1/systemone (client HTTP)
```

Le chiamate condividono il Trace ID tramite propagazione W3C `traceparent`.
Gli span includono operazione, provider `azure.ai.inference`, deployment,
modello effettivamente restituito, host/porta, protocollo, agente/azione scelti
e token comunicati dal modello. Il codice `classify` e un'operazione custom
per Decision; non sono inventati finish reason, temperatura, token massimi
o altri parametri non presenti nel protocollo.
Lo span modello riporta anche lo stato HTTP e `decision.request.id` quando
il gateway fornisce `apim-request-id`. Le credenziali non vengono registrate.
Il batch indica richieste previste, completate, fallite e annullamento.

In **Metrics**, selezionare `apiservice` e il meter `DecisionApps.Models`:

- `gen_ai.client.operation.duration`: istogramma in secondi della sola richiesta
  HTTP al modello, con lo stesso confine di `elapsedMilliseconds`;
  `error.type` distingue HTTP upstream (anche 429), timeout, rete e risposta
  non valida. Preparazione, autenticazione e mapping non entrano nella durata.
- `gen_ai.client.token.usage`: istogramma dei conteggi effettivi per richiesta,
  distinto da `gen_ai.token.type=input|output`; la somma misura i token
  comunicati per il filtro scelto.
- `decision.client.responses_without_usage`: risposte senza conteggio.
  Non si emettono misure token zero per simulare dati mancanti.

Per i consumi filtrare il livello modello (`gen_ai.operation.name=classify`):
gli attributi token degli span agente e batch sono riepiloghi delle stesse
chiamate, non consumi aggiuntivi. I token validi gia ricevuti vengono registrati
anche se la successiva validazione delle probabilita fallisce.
Le interruzioni volontarie sono marcate `decision.cancelled=true`; richieste
fallite o annullate senza usage non hanno consumi stimati. Non vengono aggiunti
retry e la telemetria non cambia la classificazione o il JSON dell'API.
Lo span `classify` e la metrica durata terminano alla ricezione del corpo HTTP,
anche se gli attributi di risposta sono aggiunti dopo la deserializzazione.
Gli span agente, API e batch conservano le proprie durate diagnostiche
end-to-end, ma non sono presentate come tempi del modello.
Se l'autenticazione fallisce prima della richiesta, l'errore appare sullo span
agente: non viene inventato uno span o un tempo di chiamata al modello.

**Privacy:** per impostazione predefinita domande, prompt e risposte testuali
non sono raccolti. Per ispezionare i messaggi nello span GenAI dell'agente,
esclusivamente con dati di test appropriati, abilitare esplicitamente
`Telemetry:EnableSensitiveData=true` sull'API (variabile
`Telemetry__EnableSensitiveData=true`) e riavviare. Questo abilita la raccolta
dei messaggi nativi di Agent Framework, non degli header di autenticazione;
il payload proprietario non e duplicato nei log HTTP.
Con questa opzione le domande e i risultati possono essere esportati e
conservati dal backend di telemetria: lasciarla disabilitata con dati reali.
Non viene creato o configurato alcun servizio Azure aggiuntivo; fuori da Aspire
l'esportazione richiede `OTEL_EXPORTER_OTLP_ENDPOINT` verso un collector
autorizzato. Le convenzioni GenAI sono in evoluzione.

## Confronto con l'ambito Excel

L'importazione legge **Foglio origine**, presente sia nello Storico sia nel
Campione 200 del file fornito. Nello Storico questa colonna identifica i nove
ambiti di test; la colonna `Ambito` contiene invece sotto-temi (es. Biglietti,
Residenza, Graduatorie) e non sostituisce `Foglio origine`.

La mappa di riferimento, derivata dalle card e confermata per il dataset, e:

| Foglio origine | Agente atteso | Competenza della card |
|---|---|---|
| Ambito AMA | Informativo | Rifiuti, TARI, raccolta e servizi AMA informativi |
| Ambito ATAC | Informativo | Biglietti, abbonamenti, sosta, regole e servizi ATAC |
| Ambito Informativo Portale | Informativo | Servizi e procedure istituzionali di Roma Capitale |
| Ambito News | Informativo | Notizie, comunicati e informazioni di servizio |
| Ambito RSM | Informativo | Informazioni istituzionali su mobilita, ZTL e viabilita |
| Ambito CRM | CRM | Segnalazioni, appuntamenti, comunicazioni e ricontatto |
| Ambito Anagrafe | Anagrafe | Posizione e certificati anagrafici |
| Ambito Scuola | Scuola | Graduatorie, quote, menu e strutture scolastiche |
| Ambito TVA | TVA | Turismo, luoghi, eventi, mobilita e informazioni real-time |

Il prefisso `Ambito` e facoltativo; confronto senza distinzione di maiuscole
e con spazi normalizzati. Non si usano match parziali o inferenze dalla domanda.
Per altri workbook, se `Foglio origine` non esiste si legge la colonna `Ambito`.
Se mancano entrambe, si usa il nome del foglio solo quando corrisponde a un
ambito noto. Colonne duplicate nella fonte scelta e formule negli ambiti
sono rifiutate. Una cella vuota nella fonte scelta resta vuota: non eredita
il valore della riga precedente o di una colonna secondaria.

Ogni domanda mostra ambito, fonte, agente atteso, agente selezionato e:

- **Corrisponde**: Decision ha scelto l'agente della mappa.
- **Non corrisponde**: Decision ha scelto un agente diverso.
- **Non confrontabile**: risultato disponibile ma ambito assente/non mappato
  (incluse le prove manuali). Nessun agente atteso viene inventato.

Il riepilogo globale (tutti i fogli e prove manuali) e quello per ambito
mostrano corrispondenze e differenze. La percentuale e
`corrispondenze / classificazioni con agente atteso * 100`.
Righe non eseguite, errori e risultati non confrontabili non entrano nel
denominatore; in assenza di confronti viene mostrato `--`, non uno 0% fittizio.
Il JSON mantiene la lista di righe e aggiunge `scope`, `scopeSource`,
`expectedAgent` e `comparison` (`matches`: true, false oppure null).

**Il riferimento non viene mai inviato a Decision**: `/api/ratings` continua a
ricevere solo la domanda, evitando che l'etichetta attesa influenzi il routing.
La mappa e una baseline di ambito, non una verita per ogni singola richiesta:
ad esempio segnalazioni AMA operative sono competenza CRM e passaggi dei mezzi
in tempo reale sono competenza TVA. Un disaccordo non dimostra da solo che
il modello abbia sbagliato. Il confronto riguarda l'agente, non l'azione o
le colonne `Esito`/`Risposta attesa` del vecchio test.

## Classificazione e sicurezza operativa

Il flusso e:

```text
Blazor -> POST /api/ratings -> AIAgent.RunAsync
       -> DecisionRoutingAgent -> Foundry systemone
       <- agent choice + action choice + probabilita
```

Il modello usa il protocollo **systemone**, non Chat Completions o Responses.
Il client invia JSON bufferizzato con `Content-Length`: il gateway rifiuta
richieste chunked prive di questo header.
Un agente personalizzato di Microsoft Agent Framework adatta questo protocollo
senza simulare un modello generativo. Ogni domanda e indipendente: niente
cronologia condivisa o contaminazione tra righe del test.

Le descrizioni dei cinque agenti provengono dalle card A2A fornite, conservate
in [AgentCards](DecisionApps.ApiService/AgentCards). Le card sono normalizzate
in UTF-8, con accenti e punteggiatura ripristinati dai file originali.
Il lettore riconosce anche il BOM per card UTF-16.
Gli URL e i tool delle card **non vengono invocati**.
Il routing utilizza descrizioni, competenze ed esclusioni:
Informativo, CRM, Scuola, Anagrafe e TVA (turismo e mobilita).

- `agent`: scelta esclusiva tra i cinque destinatari.
- `action`: `Informativa` per lettura, istruzioni, ricerca e simulazione;
  `Dispositiva` per scrittura dati, invio, emissione e prenotazione.
- Una richiesta di istruzioni su come ottenere un documento non equivale
  alla richiesta di emetterlo.
- Le due classificazioni sono indipendenti e nella stessa chiamata.
- Decision non produce spiegazioni testuali: nessuna motivazione e inventata.
- Sono rifiutate risposte incomplete, opzioni sconosciute, punteggi fuori
  intervallo, somme incoerenti e scelte non massime.
- Si ammette una tolleranza di 0,001 sulla somma delle probabilita grezze;
  solo questo piccolo errore numerico viene normalizzato.
- Le percentuali visualizzate/esportate usano il metodo dei maggiori resti
  su 10000 centesimi di punto, per sommare **esattamente a 100,00%**.
- Revisione consigliata se la probabilita massima agente o azione e sotto
  il 60%, o se il margine tra i primi due agenti e sotto 10 punti.
  Sono soglie dimostrative, non validate su un dataset etichettato.
- La scelta e forzata tra cinque agenti: una richiesta fuori dominio puo
  comunque avere un vincitore. Il rating non garantisce fattibilita o correttezza.
- Nessun retry automatico: evita chiamate fatturate duplicate. Errori di
  servizio, autenticazione e quota fermano il batch lasciando visibili i risultati
  precedenti; si possono selezionare e riprovare le righe desiderate.
- Interrompere il test annulla la richiesta locale e impedisce le successive;
  non garantisce l'annullamento della fatturazione di una richiesta gia inviata.

Nessun file o risultato e persistito dal server. Le domande restano in memoria
nel circuito Blazor e vengono inviate al solo endpoint configurato quando si
avvia il test. Con la configurazione predefinita non sono inserite nei log
applicativi o nella telemetria; l'opzione di diagnostica sensibile descritta
sopra ne abilita esplicitamente l'esportazione. L'export JSON contiene i testi:
gestirlo secondo le proprie regole di protezione dei dati.

Questa e una **demo locale priva di autenticazione applicativa**, non un servizio
da esporre pubblicamente. Prima di distribuirla aggiungere autenticazione,
autorizzazione, rate limiting e policy di gestione dei dati. Le azioni dispositive
sono soltanto classificate: nessun certificato, pratica o segnalazione e creato.

## API

| Metodo | Percorso | Scopo |
|---|---|---|
| GET | `/api/agents` | Cinque card del catalogo |
| GET | `/api/configuration` | Endpoint, deployment, tipo autenticazione; mai la chiave |
| POST | `/api/import` | Un file `multipart/form-data`; non invoca il modello |
| POST | `/api/ratings` | JSON `{"question":"..."}`; una classificazione |
| GET | `/health` | Health check locale di processo, non verifica l'accesso al modello |
| GET | `/openapi/v1.json` | Specifica API in Development |

```powershell
dotnet build .\DecisionApps.sln
dotnet test .\DecisionApps.Tests
```

Su Windows arrestare l'AppHost prima di ricompilare o eseguire i test:
i processi in esecuzione possono bloccare le DLL di output.

I test verificano importazione, limiti esatti, schema systemone, integrazione
con Agent Framework, arrotondamento a 100%, errori, assenza di retry,
mappa degli ambiti, priorita di Foglio origine e calcolo delle corrispondenze
senza contaminare l'input del modello. Usano
un trasporto HTTP di test: **non** misurano la qualita del modello reale e non
effettuano chiamate fatturate. L'applicazione non contiene una modalita mock.
I test OpenTelemetry verificano gli span e le metriche realmente esportati,
la correlazione W3C fino a un server HTTP locale, la privacy predefinita,
l'opt-in dei messaggi, gli errori e l'annullamento, senza chiamare Foundry.

Riferimento:
[Microsoft-Decision-1.1](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).
