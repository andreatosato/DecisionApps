# Pong / Decision Lab

Pong 2D locale: giocatore umano a sinistra, agente a destra, primo a **7**.
Campo nero, racchette, palla, rete tratteggiata e punteggio a pixel sono
disegnati con Canvas 2D, senza asset esterni.

## Struttura e percorso per la conferenza

Ogni parte ha una responsabilita distinta, senza nuovi framework o progetti:

```text
Pong.Web/
  Program.cs                       Avvio e middleware
  Agents/                          Agente Microsoft Agent Framework
    PongDecisionAgent.cs           Esecuzione dell'agente
    PongDecisionPrompt.cs          Istruzioni e scelte up/down/stay
    AgentRegistration.cs           Registrazione e OpenTelemetry
  Decision/                        Protocollo nativo Decision, autenticazione,
                                   mapping delle risposte e telemetria del modello
  Endpoints/                       API HTTP
  Contracts/                       Richieste e risposte
  Game/                            Validazione dello stato ricevuto
  wwwroot/
    app.mjs                        Coordinamento e ciclo di gioco
    agent/decision-controller.mjs  Osservazioni, richieste e durata delle scelte
    ui/pong-view.mjs               Canvas, pannelli e storico delle risposte
    ui/pong-input.mjs              Tastiera, pulsanti e touch
    game/physics.mjs               Fisica e regole, senza DOM o rete
```

Per raccontare la demo:

1. Partire da [Program.cs](Pong.Web/Program.cs) e dalla
   [registrazione dell'agente](Pong.Web/Agents/AgentRegistration.cs).
2. Mostrare [PongDecisionAgent](Pong.Web/Agents/PongDecisionAgent.cs) e il
   [prompt](Pong.Web/Agents/PongDecisionPrompt.cs): qui vive la logica agentica.
3. Aprire [DecisionClient](Pong.Web/Decision/DecisionClient.cs) solo per
   illustrare il trasporto nativo verso Decision 1.1; non contiene la policy.
4. Passare ad [app.mjs](Pong.Web/wwwroot/app.mjs), poi al
   [controller delle richieste](Pong.Web/wwwroot/agent/decision-controller.mjs),
   alla [vista](Pong.Web/wwwroot/ui/pong-view.mjs) e agli
   [input](Pong.Web/wwwroot/ui/pong-input.mjs).
5. Tenere la [fisica](Pong.Web/wwwroot/game/physics.mjs) separata dal modello:
   in tempo reale il gioco avanza senza attendere l'inferenza; a passi attende il modello.

Il controller JavaScript non e un secondo agente AI: gestisce le richieste
all'API senza dipendere dalla UI. L'inferenza passa dall'`AIAgent` Microsoft
Agent Framework sul server. Non sono introdotti previsione locale della
traiettoria o controller alternativi; il modello selezionato resta responsabile delle scelte.
Accelerazione del 10% per colpo, limite di 600 px/s e comportamento restano invariati.

### Scelta del modello e confronto

Prima di **INIZIA PARTITA**, la combo **MODELLO** permette di scegliere
Decision 1.1 oppure il deployment GPT configurato. La selezione resta bloccata
anche durante la pausa: usare **NUOVA PARTITA** per cambiare modello.
Il cambio azzera punteggi, storico e statistiche e richiede una nuova preparazione.
Entrambi passano dallo stesso `PongDecisionAgent : AIAgent` di Microsoft Agent
Framework, con identici stato, osservazioni, istruzioni di controllo e regole fisiche
a parita di modalita. `playMode` comunica al modello la semantica temporale scelta.
Decision usa il protocollo nativo SystemOne; GPT usa Chat Completions con output
JSON Schema strict e nessuna conversazione persistente (`store: false`).
GPT invia messaggi system/user e legge `choices[0].message.content`; i token
arrivano da `prompt_tokens`/`completion_tokens`, poi vengono normalizzati nello
stesso risultato usato da Decision. Non c'e fallback alla Responses API.
Cambiare API non garantisce una latenza inferiore: va misurata sul deployment.
Le probabilita GPT sono stime dichiarate dal modello, non logprobs o probabilita
calibrate; non sono direttamente confrontabili con quelle native di Decision.

Il pannello mostra token input/output/totali e medie del tempo HTTP e del tempo
totale browser/API della partita corrente. Sono incluse le risposte valide anche
se troppo lente per essere applicate; preparazione e test diagnostico sono esclusi.
Le risposte senza usage sono segnalate, non considerate consumo zero.
Richieste annullate, errori o risposte fuori turno possono consumare token non
conteggiati: questi numeri non sostituiscono la fatturazione Foundry.
L'output usage GPT include i reasoning token nel conteggio riportato dal provider.
Il tempo HTTP include il buffering della risposta ma esclude autenticazione e
parsing; il totale browser/API li include. I limiti restano uguali per i due
modelli: timeout HTTP 8 secondi; in tempo reale le azioni scadono dopo 350 ms.
Il browser attende fino a 45 secondi per preparazione/test (inclusa la prima
autenticazione, limitata a 30 secondi), e fino a 10 secondi per le chiamate di gioco.
Partite interattive diverse non costituiscono un benchmark deterministico.

### Modelli lenti: modalita a passi e test

La combo **MODALITA** e disponibile prima della partita per entrambi i modelli.
**Tempo reale** resta il comportamento predefinito: il campo continua a muoversi
durante la chiamata e una risposta oltre 350 ms viene scartata. Luna puo impiegare
secondi: la pallina puo superare la racchetta prima che arrivi una risposta,
provocando l'annullamento della richiesta. Rallentare solo la pallina non
risolverebbe la scadenza reale di 350 ms.

Con **A passi / modelli lenti**, durante il turno del modello l'intero campo
(entrambe le racchette incluse) resta fermo mentre si attende l'inferenza.
La scelta valida viene poi applicata per **100 ms di simulazione**, al massimo,
prima di inviare lo stato aggiornato. Fuori turno il gioco avanza normalmente.
Colpi, punti e pause interrompono l'azione; non si applicano risposte di vecchi turni.
Le velocita fisiche, l'accelerazione e la policy restano invariate: non si calcola
localmente una mossa. Lo storico usa il tempo simulato; la latenza riportata e
sempre tempo reale e non viene usata per proiettare movimento durante l'attesa.
Modello e modalita restano bloccati anche in pausa fino a **NUOVA PARTITA**.
Confrontare i due modelli nella stessa modalita; i risultati a passi non sono
un benchmark di reattivita in tempo reale.

**TEST MODELLO / CAMPO FERMO** esegue una sola inferenza prima della partita,
senza far partire la pallina. Il pannello mostra azione, probabilita, token,
deployment e durata HTTP/totale nel risultato espandibile. Non avvia richieste
successive e non sostituisce la preparazione iniziale della partita.
Errori, timeout e annullamenti del test sono visibili.

**CHIAMATE / CICLO COMPLETO** mostra subito ogni invio con il nome del modello,
il tempo di attesa aggiornato e l'esito finale, incluse richieste annullate
prima della risposta HTTP, timeout ed errori di rete. Una chiamata annullata
non viene contata come risposta ricevuta. Lo storico mantiene le ultime otto
chiamate, separato da quello delle risposte, e si azzera a nuova partita.
L'annullamento locale non dimostra che l'inferenza remota non sia stata eseguita
o fatturata.

## Avvio

Prerequisiti: .NET 10 SDK e accesso a un deployment **Microsoft-Decision-1.1**.
Per il confronto GPT serve anche il deployment chat configurato sotto.
Non occorrono Docker o database; il percorso Decision non richiede un modello chat.

Dalla root del repository:

```powershell
az login
dotnet run --project .\SRC\Pong\Pong.AppHost
```

Aprire la risorsa **pong** dalla dashboard Aspire. L'AppHost usa porte diverse
da ChatBot; le due app possono funzionare contemporaneamente.
In alternativa, senza dashboard:

```powershell
dotnet run --project .\SRC\Pong\Pong.Web
```

Il caricamento della pagina legge solo la configurazione; le inferenze iniziano
premendo **INIZIA PARTITA** oppure **TEST MODELLO** e possono consumare token a pagamento.
Al primo avvio della pagina compare **WAITING / DECISION**: una chiamata reale
prepara autenticazione e modello mentre il campo resta fermo al servizio.
La partita inizia solo dopo una risposta valida, non dopo un timer.
La risposta di preparazione compare nello storico come `#0 / PREPARAZIONE`,
ma non muove la racchetta e non alimenta la stima di latenza degli scambi.
Pausa, cambio scheda e reset annullano l'attesa; errori e timeout restano
visibili e richiedono un nuovo avvio manuale.
Una volta pronto, il modello non viene preparato di nuovo per pause o nuove
partite nella stessa pagina, salvo cambio modello. Ricaricando la pagina si ripete la preparazione.

### Configurazione

Il servizio usa `Decision:Endpoint` (URL HTTPS completo che termina con
`/providers/microsoft/v1/systemone`) e `Decision:Deployment` (nome esatto del
deployment). I valori iniziali sono gli stessi della demo ChatBot.
Le configurazioni delle due app restano indipendenti.

Per GPT sono configurati `Gpt:Deployment` = `gpt-6-luna` e `Gpt:Endpoint` =
`https://agenteinformativo.services.ai.azure.com/openai/v1/chat/completions`.
Il deployment deve supportare Chat Completions e gli output strutturati.
Il deployment deve gia esistere: l'app non lo crea e non verifica la disponibilita
al caricamento della pagina. Un nome errato o permessi mancanti producono un errore
visibile alla preparazione, senza ripiegare su Decision.
Per disabilitare GPT, impostare `Gpt:Deployment` a stringa vuota: la relativa
opzione rimane visibile ma non selezionabile.
GPT usa lo stesso token Entra in cache, oppure una chiave separata `Gpt:ApiKey`
nei user-secrets di Pong.Web. Sono supportate `Gpt__Endpoint`, `Gpt__Deployment`
e `Gpt__ApiKey`; nessuna credenziale viene inviata al browser.

L'autenticazione predefinita e `DefaultAzureCredential`, con scope
`https://cognitiveservices.azure.com/.default`.
Il token di accesso viene condiviso e conservato solo in memoria nel processo
fino al rinnovo (due minuti prima della scadenza, o al `RefreshOn` del provider).
Non si rilancia l'acquisizione delle credenziali per ogni snapshot. Le richieste
contemporanee condividono un solo rinnovo; l'annullamento di un punto interrompe
solo l'attesa di quel chiamante, non l'autenticazione condivisa, che ha un limite
di 30 secondi e viene annullata allo spegnimento del servizio.
La prima autenticazione puo comunque richiedere diversi secondi: questa attesa
avviene durante la preparazione iniziale, prima di mettere la palla in gioco.
Si memorizza il token, mai le decisioni: ogni snapshot continua a invocare
realmente il modello.
L'identita deve gia avere i permessi di inferenza sulla risorsa:
questa app non crea risorse, deployment o assegnazioni di ruolo.

Per una API key, usare esclusivamente i user-secrets di **Pong.Web**:

```powershell
dotnet user-secrets set "Decision:ApiKey" "<API-KEY>" --project .\SRC\Pong\Pong.Web
```

Sono supportate anche le variabili d'ambiente `Decision__Endpoint`,
`Decision__Deployment` e `Decision__ApiKey`. Non inserire chiavi nel repository.
Le credenziali rimangono nel backend e non vengono inviate al browser.
I user-secrets di ChatBot non vengono copiati.

## Come giocare

- **W / S** oppure **freccia su / giu**: muovi la racchetta sinistra.
- **Trascina sul campo** con mouse o touch come alternativa alla tastiera.
- **Spazio** o **PAUSA**: interrompi/riprendi.
- **NUOVA PARTITA**: azzera punteggi e statistiche, senza avviare altre inferenze.
- Perdere il focus o cambiare scheda mette in pausa gioco e nuove richieste.
- Dopo ogni punto la palla torna al centro e il nuovo servizio parte subito,
  senza conto alla rovescia. Il primo servizio attende solo la preparazione AI.
- In **tempo reale** la fisica non attende mai un'inferenza: palla e comandi
  umani continuano durante l'elaborazione. Una risposta lenta puo far perdere
  il punto al modello, ma non mette il campo in pausa.

La fisica gira nel browser, separata dalla frequenza delle decisioni:
movimento continuo, rimbalzi sui bordi superiore/inferiore, collisioni con le
racchette, velocita progressiva e punteggi. La palla parte da **220 px/s**;
ogni colpo di una delle due racchette aumenta il modulo della velocita del
**10%**, fino a **600 px/s**. I rimbalzi sui bordi non accelerano la palla.
Dopo un punto il nuovo servizio riparte da 220 px/s.
Il pannello **Velocita totale palla** mostra il modulo
`sqrt(velocityX^2 + velocityY^2)`: una componente X/Y puo diminuire al cambio
di angolo anche se la velocita totale e aumentata.
Le coordinate logiche sono **960 x 600**, indipendenti dalla dimensione dello
schermo. L'asse Y cresce verso il basso; posizione della racchetta = suo centro.

## Chi decide

```text
Canvas: stato + osservazioni reali con timestamp + latenza misurata
  -> POST /api/decisions
    -> Microsoft Agent Framework AIAgent.RunAsync
      -> agente Pong personalizzato
        -> Decision 1.1, endpoint nativo systemone
      <- scelta esclusiva up/down/stay e tre probabilita
  <- applicazione temporanea del movimento alla racchetta destra
```

Il modello riceve lo stato numerico, compresi velocita X/Y, dimensioni della
palla, posizione/dimensioni/velocita della racchetta e orizzonte di movimento.
Le istruzioni chiedono di considerare traiettoria e rimbalzi. **Non calcoliamo
localmente un punto d'intercetto per scegliere la mossa**: solo Decision sceglie
`up`, `down` o `stay`. Anche la scelta di restare fermo viene dal modello:
non si ricava localmente una zona di allineamento o una direzione sostitutiva.
La correttezza o abilita della strategia non e garantita.

Ogni richiesta del browser include fino a **cinque osservazioni reali**,
dalla piu vecchia alla piu recente, campionate ogni almeno 40 ms; lo snapshot
della richiesta viene sempre incluso anche se cade prima del prossimo campione.
Ogni osservazione contiene `timestampMilliseconds`, `ballX`, `ballY`,
`velocityX`, `velocityY` e `paddleY`. L'ultimo campione coincide esattamente
con lo stato corrente e con `observedAtMilliseconds`. In tempo reale i timestamp
sono millisecondi monotoni del browser; a passi sono millisecondi di simulazione
che non avanzano durante l'inferenza. Non sono orari UTC.
Il buffer contiene solo l'ultimo tratto osservato: si azzera a un cambio di
velocita/rimbalzo, fuori turno, al punto, in pausa e a nuova partita. I campioni
piu vecchi di un secondo vengono rimossi. Non si aggiungono posizioni future,
fit, intercetti o traiettorie calcolati dal programma.

`estimatedResponseMilliseconds` comunica al modello la latenza totale misurata:
e `null` prima della prima risposta valida; poi segue una media esponenziale
con peso 25% all'ultima misura e 75% alla stima precedente. La nuova partita
la azzera. In tempo reale **la compensazione e interamente affidata al modello**: riceve
la misura e lo storico del movimento della racchetta, non una proiezione
locale delle posizioni. Le istruzioni gli chiedono di valutare da solo i
rimbalzi, il tempo di risposta e quando `stay` evita un superamento.
Le richieste rimangono indipendenti attraverso Microsoft Agent Framework:
lo storico numerico viaggia nel payload, senza una conversazione crescente
o memoria persistente lato server. L'accelerazione e **+10% / 600 px/s**.

Le chiamate iniziano solo quando il centro della palla e nella **meta destra**,
con `velocityX > 0`, prima di superare il piano di collisione della racchetta.
Il modello usa anche `velocityY` per valutare la direzione verticale e i
rimbalzi futuri, non soltanto l'altezza corrente. Durante tutta la permanenza
della palla in arrivo nella meta destra, ogni nuova chiamata riprocessa lo
stato corrente e puo cambiare la direzione della racchetta: non si prende
una sola decisione al passaggio della linea centrale.
Dopo aver colpito la palla la racchetta destra abbandona subito la vecchia
azione e torna automaticamente al centro (Y=300) a **320 px/s**, senza
teletrasporto o oscillazioni. Questo e un riposizionamento fisso locale,
non un'inferenza o una previsione della traiettoria: nessuna chiamata a
Decision mentre attende l'avversario. Raggiunto il centro, resta ferma.
Riparte quando la palla restituita dall'umano rientra nella meta destra.
Se il rientro non e ancora terminato, il nuovo turno del modello ha priorita.
Le richieste pendenti sono annullate e le risposte del turno precedente ignorate.
Questa condizione e verificata anche dall'API/agente/client: uno stato fuori
turno e rifiutato senza autenticazione o chiamata al modello.

Il protocollo Decision non e Chat Completions: per Decision l'agente usa un client HTTP
tipizzato per l'endpoint nativo, con richieste bufferizzate e `Content-Length`.
Il percorso Decision non richiede un modello generativo e non inventa spiegazioni.
Riferimenti:
[API ufficiale Decision](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision)
e [esempio Microsoft di AIAgent con Decision](https://github.com/microsoft/Generative-AI-for-beginners-dotnet/tree/main/samples/CoreSamples/DecisionAgent-01-FoundryHosted).

### Latenza, costi ed errori in tempo reale

- Al massimo una richiesta in corso per pagina, avviata non piu spesso di una
  ogni **100 ms** durante il solo turno del modello (fino a **10 richieste/s**,
  meno con latenza elevata). Dopo una risposta, il primo frame utile invia
  lo stato aggiornato se l'intervallo e trascorso. Fuori turno: zero richieste.
  La frequenza maggiore puo aumentare il consumo di token rispetto a prima;
  non riduce la latenza del deployment.
- La cadenza nominale rimane **100 ms**, ma la racchetta mantiene l'ultima
  scelta valida tra due risposte, senza fermarsi artificialmente dopo 100 ms.
  La nuova scelta sostituisce subito la precedente, anche invertendola.
  Il movimento scade comunque **350 ms dallo snapshot inviato**, non
  dall'arrivo della risposta: una risposta lenta non prolunga l'uso di uno
  stato vecchio. Se la prossima inferenza tarda, la racchetta si ferma con
  l'indicazione **SCELTA SCADUTA**, ma la palla continua. Non c'e un movimento
  euristico sostitutivo. Questa continuita elimina i buchi fra risposte,
  ma non riduce la latenza di inferenza del deployment.
  Colpo, punto, pausa e cambio turno interrompono subito la vecchia azione;
  il rientro al centro dopo il colpo resta separato.
- Risposte di un punto/partita precedente, o annullate in pausa, sono ignorate.
- Una risposta arrivata dopo oltre **350 ms** e scartata con l'indicazione
  **RISPOSTA SUPERATA / STATO SUPERATO**: non viene applicata alla racchetta e
  la palla non si ferma. Il ciclo successivo invia un nuovo snapshot, non un
  retry della vecchia richiesta. Statistiche e token della risposta ricevuta
  restano visibili anche se la mossa non viene applicata.
- Errori di autenticazione, quota, rete o risposta fermano il gioco; si riprende
  manualmente dopo averne risolto la causa. Nessun fallback e nessun retry
  automatico che possa moltiplicare le chiamate fatturate.
- La pausa annulla la richiesta locale ma non garantisce l'annullamento
  dell'inferenza gia partita o della sua fatturazione.

Il pannello mostra stato corrente della palla, ultima scelta, probabilita,
numero di risposte valide, durata HTTP comunicata dal server e token
dell'ultima risposta. `--`/`non disponibili` indica dati non comunicati,
non uno zero stimato. Le probabilita si riferiscono allo snapshot inviato,
non necessariamente alla posizione attuale mostrata.
Il tempo della chiamata comprende rete/gateway e lettura della risposta:
non e il tempo puro di calcolo interno del modello. **Tempo risposta totale**
misura invece l'intero giro browser/API, inclusa autenticazione e orchestrazione:
e il tempo effettivo che rende obsoleto uno snapshot, non la sola durata HTTP
del modello. Se supera spesso 350 ms, il deployment o l'autenticazione non
stanno tenendo il passo con il gioco in tempo reale.

Le tre barre mostrano **SU / GIU / FERMO**. Il dettaglio espandibile
**DATI INVIATI A DECISION** mostra il numero di osservazioni, la latenza
stimata disponibile al momento della richiesta e i campioni realmente inviati.
Non e una spiegazione generata del ragionamento del modello.

**RISPOSTE IN ARRIVO** evidenzia ogni risposta HTTP ricevuta, anche se il
modello ripete la stessa direzione: contatore, numero della richiesta e
orario locale fino ai millisecondi. Lo storico mostra le **ultime otto**
risposte, dalla piu recente, con direzione, tempo totale e uno degli esiti:
**APPLICATA**, **SCARTATA: SUPERATA**, **SCARTATA: FUORI TURNO** oppure
**ERRORE** (HTTP o payload non valido). Una richiesta solo inviata non
aggiorna l'orario. Le richieste annullate senza risposta ricevuta non
incrementano il contatore.
Il contatore delle risposte comprende anche scarti ed errori HTTP; quello
delle decisioni mantiene il significato di risposte valide nel turno corrente.
Lo storico rimane consultabile in pausa e si azzera con **NUOVA PARTITA**.
Non viene salvato dal server o esportato in telemetria. L'evidenziazione
discreta delle nuove righe rispetta la preferenza di movimento ridotto.

Aspire raccoglie log e tracce; l'invocazione Agent Framework usa la sorgente
`Pong.Agents` e il trasporto nativo `Pong.Models`. La cattura dei contenuti
sensibili dell'agente e disattivata.

### OpenTelemetry GenAI in Aspire

Nella dashboard selezionare **Traces**, filtrare la risorsa **pong** e aprire
una richiesta `POST /api/decisions`. La gerarchia e:

```text
POST /api/decisions
  invoke_agent PongDecisionAgent          (Microsoft Agent Framework)
    classify Decision-1.1                (client GenAI nativo)
      HTTP POST .../v1/systemone          (strumentazione HttpClient)
```

Agente e modello condividono il Trace ID e il modello ha come parent lo span
dell'agente. `classify` e un'operazione custom: non si simula una Chat
Completions. Lo span modello espone:

- `gen_ai.operation.name=classify`, `gen_ai.provider.name=azure.ai.inference`;
- `gen_ai.request.model` (deployment), `gen_ai.response.model` quando comunicato;
- `server.address`, `server.port`, `decision.protocol=systemone`;
- `http.response.status_code`, `decision.request.id` dal gateway se disponibile;
- `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens` quando comunicati;
- `decision.action=up|down|stay`, `decision.model.duration_ms`;
- `error.type` per HTTP upstream, timeout, rete, risposta non valida o eccessiva;
  `decision.cancelled=true` distingue gli annullamenti del chiamante.

Lo span agente conserva provider, deployment, eventuale modello effettivo,
azione e usage tramite il middleware Agent Framework.
Un errore di autenticazione precedente all'HTTP e attribuito all'agente
(`error.type=authentication_error`), senza inventare uno span di inferenza.

In **Metrics**, selezionare la risorsa **pong**, meter **Pong.Models**:

| Metrica | Unita | Significato |
|---|---|---|
| `gen_ai.client.operation.duration` | secondi | Istogramma della sola richiesta HTTP bufferizzata |
| `gen_ai.client.token.usage` | token | Istogramma dei conteggi effettivi, separati da `gen_ai.token.type=input|output` |
| `decision.client.responses_without_usage` | risposte | Risposte valide prive di usage |

Le serie includono operazione, provider, deployment e host/porta; il modello
restituito e aggiunto quando presente. Durate fallite hanno `error.type`,
quelle annullate `decision.cancelled`. Gli errori e le risposte non valide
non creano misure di token o falsi conteggi zero; uno zero comunicato
esplicitamente dal modello e invece conservato.
La durata e lo span modello terminano alla ricezione del corpo HTTP:
autenticazione, serializzazione iniziale e mapping finale sono esclusi.
Gli errori che precedono l'HTTP non producono misure di durata modello.

Coordinate, velocita, prompt, risposte integrali, chiavi e token di accesso non
sono inclusi nella telemetria applicativa. Azione, sequence e ID richiesta
non vengono usati come dimensioni metriche, per evitare serie ad alta cardinalita.
I test verificano anche l'export effettivo tramite la pipeline dei Service
Defaults, oltre a correlazione, metriche, errori, cancellazione e privacy.

## API

| Metodo | Percorso | Scopo |
|---|---|---|
| GET | `/api/configuration` | Deployment e tipo di autenticazione, mai la chiave |
| POST | `/api/decisions` | Uno stato di gioco; una scelta del modello |
| GET | `/health` | Health check del processo in Development, non test del modello |

Esempio di richiesta (coordinate dei centri):

```json
{
  "sequence": 1,
  "arena": { "width": 960, "height": 600 },
  "ball": { "x": 500, "y": 300, "velocityX": 198, "velocityY": 95, "radius": 8 },
  "paddle": { "x": 926, "y": 300, "width": 14, "height": 88, "speed": 320 },
  "decisionIntervalSeconds": 0.1,
  "observedAtMilliseconds": 100,
  "estimatedResponseMilliseconds": null,
  "history": [
    { "timestampMilliseconds": 50, "ballX": 490.1, "ballY": 295.25,
      "velocityX": 198, "velocityY": 95, "paddleY": 300 },
    { "timestampMilliseconds": 100, "ballX": 500, "ballY": 300,
      "velocityX": 198, "velocityY": 95, "paddleY": 300 }
  ]
}
```

I client precedenti possono omettere tutti e tre i campi di contesto.
Se `history` e presente, deve contenere da 1 a 5 campioni completi, finiti,
ordinati strettamente per timestamp e vecchi al massimo un secondo.
L'ultimo deve coincidere con stato e timestamp correnti. Una latenza presente
deve essere finita e compresa tra 0 e 10000 ms. Contesti incompleti o incoerenti
ricevono HTTP 400 prima di autenticazione e inferenza; il limite resta 8 KiB.

La risposta include `sequence`, `action` (`up`/`down`/`stay`), `probabilities`
con tutte e tre le opzioni, normalizzate e coerenti con quella scelta,
`elapsedMilliseconds`, `deployment` e `usage` opzionale. Errori espliciti in
Problem Details non vengono trasformati in una mossa riuscita.

## Verifica offline

Dalla root:

```powershell
dotnet build .\SRC\Pong\Pong.slnx
dotnet test .\SRC\Pong\Pong.Tests
node --test .\SRC\Pong\Pong.BrowserTests\agent.test.mjs .\SRC\Pong\Pong.BrowserTests\game.test.mjs .\SRC\Pong\Pong.BrowserTests\startup.test.mjs
```

Node.js serve solo per i test JavaScript, non per eseguire il gioco.
I test backend usano trasporto e credenziali fittizi, non Azure.
I test JavaScript verificano il controller Decision senza DOM, fisica, collisioni, punteggi, limiti, geometria
inviata, osservazioni reali e limitate, latenza misurata, applicazione di `stay`,
validazione delle tre probabilita, assenza di inferenze fuori turno e rientro automatico al centro dopo
il colpo, a velocita limitata e senza applicare risposte obsolete.
Non dimostrano l'accuratezza del modello reale.

Questa e una **demo locale senza autenticazione applicativa**. Non esporla
pubblicamente senza aggiungere autenticazione, autorizzazione e limiti di costo
e traffico per utente. Non si persiste lo stato delle partite sul server.
