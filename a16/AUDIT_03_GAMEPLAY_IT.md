# Revisione 0.3 — feedback BOTW su iPad A16

Base provata dall'utente: IPA costruita dal commit `1299c57b66ea7fc38598d5d751fa5b83728cc80c`.
Segnalazioni: artefatti viola, scatti/cali persistenti, impostazioni non applicate,
menu di uscita e informazioni delle prestazioni poco affidabili su iPad.
Vincolo conservato: scala 1×; nessun abbassamento automatico della risoluzione.

## Riscontri prima degli interventi

| Riscontro nel sorgente | Intervento deciso |
| --- | --- |
| `Setting.bool` ignora il default fornito e restituisce false quando manca una chiave. Questo riguarda anche il pulsante menu e le informazioni RAM | Rispettare default e fallback globali; test Swift sui valori reali di NativeSettingsManager |
| Il pulsante menu dipende da un'opzione chiamata screenshot, sparisce dopo cinque secondi e richiede tocchi non sempre intercettati col controller | Comando menu persistente con area tocco accessibile, indipendente dall'HUD; impostazioni e ritorno alla libreria raggiungibili dal gioco |
| Il binding per gioco usa Options() invece delle impostazioni globali; il setter delle impostazioni correnti modifica anche quelle globali | Ereditarietà esplicita, salvataggio dei soli override per nuovi file, compatibilità con file precedenti, comando per tornare ai globali |
| startGame rilegge il file mentre può essere ancora pendente un salvataggio con debounce | Completare i salvataggi prima di caricare la configurazione di avvio |
| Il core invia statistiche con intervallo nominale 1/60 s, con più pubblicazioni SwiftUI a ogni pacchetto | Portare la telemetria a 2 Hz, senza cambiare il ritmo del gioco; usare un solo aggiornamento del modello |
| Il pacchetto statistiche contiene 8 byte non inizializzati; Swift legge Double all'offset disallineato 17 | Formato versionato, tutti i byte scritti, lettura unaligned e controllo delle lunghezze |
| Exit dispone Device prima di arrestare i loop; StopEmulation lo dispone di nuovo; Execute lo dispone ancora. Non viene chiamato DisposeGpu | Richiedere prima lo stop, risvegliare il rendering in pausa, attendere i loop e disporre una volta; pulizia GPU come nel frontend desktop, con salvataggio finale cache |
| Il timer PerformanceStatistics mantiene riferimenti dopo la fine del gioco | Dispose esplicito e idempotente |
| L'opzione ricompressione OFF non disabilita il percorso BC→ETC2/EAC | Rendere effettivo OFF anche per quel percorso; due prove separate per ricompressione e async, con costo RAM/compilazione dichiarato |
| La conversione di texture ricreate/rilette può ripetere lavoro CPU molto costoso | Cache su disco delle conversioni, basata sul contenuto e sui parametri, con checksum, budget disco e coda scritture limitata; nessun fsync o encoding nuovo sul thread grafico |
| Fast math viene forzato a 1 anche quando il driver usa la politica predefinita che rispetta i controlli float | Conservare la configurazione ottenuta da MoltenVK, senza forzare fast math |

Gli artefatti viola **non sono ancora attribuiti con certezza** a uno di questi
percorsi: mancano cattura GPU, screenshot e log della partita provata. Le nuove
opzioni texture e shader permettono un confronto reale; non va presentata come correzione
visiva già confermata sull'iPad. Disabilitare async può aumentare pause al primo
incontro con shader nuovi: il riuso delle cache è distinto dalla compilazione.

## Progetti e fonti consultati

- Ryujinx desktop, `Systems/AppHost.cs`: ordine di arresto, DisposeGpu e applicazione
  delle impostazioni a runtime. Riutilizzare il comportamento del frontend originale
  è preferibile a liberare risorse mentre il rendering le usa.
- [MeloVertex: impostazioni in gioco](https://github.com/VertexSelection/MeloVertex/blob/XC-ios-ht/src/MeloNX/MeloNX/App/UI/Emulation/EmulationView/InGameSettingsManager/InGameSettingsManager.swift):
  l'idea di applicazione esplicita è utile, ma l'API legacy chiamata da quel ramo
  non è presente nel core attuale. Implementare un ponte coerente e applicare
  solo i parametri supportati, dichiarando quelli che richiedono riavvio.
- [Fork GUTY345: overlay](https://github.com/GUTY345/melonx-fork/blob/main/src/MeloNX/MeloNX/App/UI/Emulation/EmulationView/PerformanceDisplay/PerformanceOverlay.swift):
  il modello resta utile come riferimento d'interfaccia; non copiare polling e
  osservatori senza gestione del loro ciclo di vita.
- [MoltenVK #2148](https://github.com/KhronosGroup/MoltenVK/issues/2148) e
  [#2318](https://github.com/KhronosGroup/MoltenVK/issues/2318): segnalazioni di
  texture compresse scorrette, non prove della causa di BOTW su A16.
- [MoltenVK #2885](https://github.com/KhronosGroup/MoltenVK/issues/2885): regressione
  di durata delle viste nella 1.4.2. Non sostituire alla cieca il MoltenVK 1.4.0
  personalizzato della fork con una libreria più recente.

La ricerca generale sul web non ha risposto in questa sessione; sono stati
consultati direttamente sorgenti e issue originali su GitHub. Nessun benchmark
di un altro progetto/dispositivo viene attribuito a questa fork.

## Criteri di verifica

Compilazione del core, test del protocollo e delle cache, test Swift per default
ed ereditarietà, controlli Swift contro l'SDK iOS. L'effetto effettivo su
BOTW richiede una nuova prova: stesso tragitto e mod, 1×, confronto freddo/caldo,
p95/p99 CPU e picchi, cache hit, memoria e stato termico. Nessuna promessa di
eliminare tutti gli scatti o mantenere un FPS fisso sull'A16 senza misure.

## Esito di implementazione

- Menu persistente sopra controller e caricamento; uscita con attesa del core e
  pulizia GPU/cache. I driver di input condivisi restano disponibili per il gioco
  successivo; i manager legati al singolo titolo e il timer statistiche vengono liberati.
- Parametri grafici supportati inoltrati al backend con `BackgroundContextAction`:
  nella fork i setter di `ThreadedWindow` sono vuoti. Non basta chiamarli.
- Ridotto anche il polling HID di fallback su A16 da 1 ms a 4 ms (250 Hz);
  gli eventi dei controller continuano a risvegliare subito il loop. Il fallback
  può aggiungere fino a 3 ms di attesa rispetto a prima quando non arriva un evento.
- HUD a 2 Hz, RAM a 1 Hz solo quando visibile, decoder versionato unaligned.
  I picchi CPU sono intervalli di presentazione del renderer, non tempi GPU o
  del pannello. I campioni sono per finestre di circa un secondo.
- Registrazioni callback sostituite e ritirate aspettando quelle già in corso;
  Swift rilascia il contesto soltanto dopo il ritorno della rimozione nativa.
  Il modello del caricamento non resta trattenuto a ogni avvio.
- Cache conversioni v3: 256 MiB su disco, massimo 4096 file; input/output da
  4 KiB a 4 MiB, massimo due scritture in attesa e 8 MiB di dati in coda/in corso.
  Scrittore dedicato; lettura e hash restano sincroni e vanno valutati sul dispositivo.
  Quando l'headroom è sotto 256 MiB non si accodano copie nuove. Il budget della
  coda non è un limite all'intera RAM del processo o al pool di array.
  La cache vale soltanto per i percorsi BC→ETC2/EAC e non è una cache shader.

Le prove della correttezza delle conversioni confrontano BC2/BC3/BC7 con decode
completo + encoder ETC2 originale, incluse dimensioni non multiple di quattro,
mip piccoli, profondità e layer. Un confronto byte per byte non verifica il
campionamento del driver Metal/MoltenVK e non dimostra l'assenza del viola in BOTW.

La compilazione iOS viene controllata con `xcodebuild build`, senza archive,
confezionamento o pubblicazione di un IPA. Restano distinti controllo del sorgente
e misure di fluidità del gioco sull'hardware.
