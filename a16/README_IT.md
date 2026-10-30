# MeloNX A16 Lab 0.3 — candidata sperimentale

Fork del sorgente completo ufficiale **MeloNX 2.7**, commit
`13700519f3abc4e6d7ed8a065b118b2cc59bcd67` (tag annotato
`e91d806742874ec2a98835a23a2f85b668760dba`). Obiettivo: ridurre attese e
irregolarità dei frame su iPad A16 mantenendo scala 1×. Non sono ancora stati
misurati miglioramenti sull'iPad e non è una release stabile.

**Repository pubblica:** https://github.com/GDS0202/MeloNX-A16-Lab

**[Audit completo, fonti e decisioni prima degli interventi](AUDIT_2026-10-30_IT.md)**.
L'iPad A16 ha 5 core CPU e 4 core GPU: il dimensionamento non assume i 6 core
CPU degli iPhone A16. Le scelte sperimentali rimangono distinguibili dalle
correzioni di stabilità.

## Novità 0.3: feedback della prima IPA

La build dal commit `1299c57` ha avviato BOTW sull'iPad dell'utente; sono stati
segnalati viola e scatti. Questa revisione affronta difetti verificati nel
sorgente; il miglioramento della partita e la correzione visiva non sono ancora
confermati sul dispositivo. [Audit di questa revisione](AUDIT_03_GAMEPLAY_IT.md).

- Menu sempre visibile nell'angolo superiore sinistro, sopra il controller.
  Include impostazioni del titolo, informazioni prestazioni e ritorno alla libreria.
- Arresto ordinato del core e della GPU con salvataggio finale cache, senza liberare
  la superficie Metal mentre è usata. Driver condivisi disponibili per l'avvio successivo.
- Impostazioni per gioco ereditate dai globali; nuovi file salvano gli override
  effettivi tramite metadati compatibili col vecchio formato. I vecchi snapshot
  espliciti sono conservati: usare `Use global settings for this game` per azzerarli.
- Risoluzione, anisotropico, AA, filtro, aspect ratio, volume e async aggiornabili
  alla chiusura delle impostazioni. Formati texture, CPU, memoria e altri parametri
  richiedono il riavvio del gioco; le due opzioni A16 richiedono riavvio dell'app.
- Telemetria 2 Hz con p95/p99, massimo e intervalli CPU >100 ms, hit delle texture
  convertite, memoria disponibile, temperatura e Low Power Mode. Nessun cap FPS
  introdotto; nessuna riduzione automatica sotto 1×.
- Cache conversioni texture opzionale, attiva su A16 rilevato: 256 MiB su disco,
  scritture fuori dal rendering, checksum e fallback normale per errori/corruzione.
  Riduce conversioni ripetute quando c'è un hit; non evita il primo shader nuovo.
- Polling input di fallback a 250 Hz su A16 con risveglio immediato per gli eventi;
  rimossi task per ogni movimento touch e osservatori che restavano attivi dopo l'uscita.

### Prova degli artefatti viola

1. Conservare salvataggi e mod attuali e usare sempre lo stesso punto della mappa.
2. Applicare `Texture compatibility test — 1×`, uscire dal gioco e riavviarlo:
   ricompressione OFF ora disabilita davvero BC→ETC2/EAC. Può usare più RAM.
3. Se il viola persiste, provare anche `Shader correctness test — 1×` e riavviare:
   async OFF può aumentare le pause con shader nuovi. Non è il preset più veloce.
4. `Apply A16 Performance – 1×` ripristina il preset veloce con ricompressione e
   async ON. Le prove sono diagnostiche: non dichiarare risolto il viola senza verifica.

Per la fluidità usare due passaggi sullo stesso tragitto e confrontare il secondo,
a temperatura simile, annotando picchi CPU e hit. Non scaricare cache di altri:
questa cache nasce solo dai dati convertiti localmente. La scala 1× handheld
corrisponde al target 720p; la risoluzione dinamica interna scelta da BOTW resta
indipendente dall'emulatore.

## Novità 0.2

- **Carichi CPU su A16:** riconoscimento del nome della GPU Metal e Apple8,
  feedback reale di temperatura e risparmio energetico. La conversione BC→ETC2
  usa al massimo due partecipanti, uno quando caldo o in Low Power Mode.
  I compilatori distinguono dipendenze del renderer, precompilazione e manutenzione
  tramite QoS Apple; un fallimento dell'API usa le priorità gestite e viene registrato.
  Non vengono forzati core specifici o modificate frequenze/istruzioni guest.
- **SIMD 128 bit:** espansione della palette BC1/BC2/BC3 anche sui backend senza
  AVX2, incluso ARM64. Il confronto di esattezza è verificato su x64 con SIMD128
  forzato e con fallback scalare; il guadagno e il codice NativeAOT su A16 sono
  ancora da misurare. Il percorso nativo BC del driver, se disponibile, rimane
  preferibile e non viene disabilitato.
- **Meno garbage durante i draw:** rimossi gli array temporanei a un elemento
  nel metodo di aggiornamento dei descrittori; controllo dell'IL compilato
  disponibile nei test. Corretti i controlli di span vuoti nel fallback dummy.
- **JIT:** i lock della cache vengono rilasciati anche se una callback lancia
  un'eccezione, Count è sincronizzato, la registrazione del thread viene pulita
  in finally e l'inizializzazione dell'allocatore condiviso è serializzata.
- **Memoria:** il budget del processo viene riletto alla creazione delle cache;
  un primo valore 0 non resta memorizzato. Verificata la lunghezza restituita da
  task_info prima di leggere il footprint. Un evento Critical può superare il
  cooldown di un Warning precedente.
- **Vulkan:** controllo vendor/device/UUID dell'header prima di caricare una
  cache; preferenza per tre immagini swapchain MoltenVK entro i limiti dichiarati.
- **Diagnostica:** ai dati A16Pacing si aggiungono temperatura (0 nominal,
  1 fair, 2 serious, 3 critical), Low Power Mode, footprint/headroom, contatori GC
  cumulativi e code/azioni dei compilatori. Non sono misure del tempo GPU.

`A16 CPU workload control`, vicino al preset, vale per **tutti i giochi** e
richiede riavvio dell'app. Disattivandolo si ripristina la concorrenza texture e
la gestione tradizionale della priorità; le correzioni di stabilità restano.
Non cambiano automaticamente risoluzione, salvataggi o impostazioni del titolo.
Il limite di concorrenza può allungare la conversione di singole texture: va
valutato insieme alla regolarità dei frame.

## Modifiche effettive

- La compilazione segnala una pipeline pronta **prima** di effettuare manutenzione
  della cache. La manutenzione continua sul medesimo thread proprietario, prima
  del suo lavoro successivo, rispettando la sincronizzazione Vulkan. Il rendering
  può proseguire durante serializzazione e sincronizzazione del file; un lavoro
  successivo su quel thread può ancora attendere.
- In presenza di pipeline urgenti in coda, i checkpoint facoltativi vengono
  rinviati. Verifica e riciclo delle cache worker restano attivi. Il budget
  configurato rimane 256 MiB; non è un limite rigido dell'intero processo.
- Il conteggio delle pipeline salvate avanza solo dopo una scrittura riuscita.
  I tentativi di checkpoint, inclusi i falliti, sono distanziati; un errore di
  manutenzione non annulla una pipeline già compilata.
- Nelle impostazioni grafiche **del gioco**, `Apply A16 Performance – 1×` imposta
  handheld 1×, cache shader/PTC, ricompressione texture, budget memoria e Macro HLE
  attivi, compilazione asincrona, bilineare, anisotropico 1× e AA dell'emulatore
  disattivato. Riduce i log di debug e salva le impostazioni sul titolo.
- Una riga `A16Pacing` ogni circa 10 secondi registra media, bucket p95/p99,
  massimo e intervalli oltre 50/100/250 ms. L'accumulo usa memoria fissa e non
  alloca a ogni frame. I report formattati vengono generati periodicamente.
- Corretto il feed di LibHac mantenendo la dipendenza originale
  `0.21.0-alpha.129`, senza sostituirla con una versione precedente.

La cache Vulkan e la cache shader del gioco sono distinte. Riciclare una cache
Vulkan non distrugge tutte le pipeline già create. La presenza di ricicli non
dimostra, da sola, la causa degli scatti di BOTW.

## Cosa è verificato

Per 0.3 sono passati localmente 20 controlli C# e 6 Python: scheduling,
cache, JIT, default delle politiche host, conversioni, protocollo, callback e
formati. Le conversioni streaming BC2/3/BC7 coincidono col riferimento su mip,
layer e bordi parziali. I controlli Swift e la compilazione iOS vengono eseguiti
in CI con il vero SDK; l'esito aggiornato è nella scheda Actions del commit.

La prima IPA 0.2 è stata compilata e provata, con gli artefatti e i cali segnalati.
La verifica 0.3 usa `xcodebuild build` senza archive o IPA. Non misura BOTW e
non verifica JIT, entitlement, memoria, controller o driver sul dispositivo.
Lo script IPA manuale esistente resta disponibile per una successiva compilazione
richiesta dall'utente, ma il suo nome di output storico contiene ancora 0.2:
identificare questa revisione dal commit e dal build number 27103.

## Controllo e confronto

Il workflow offre `control`: ripristina scheduler, cache driver, decoder,
transcoder e aggiornamento descrittori della 2.7, disattiva la politica CPU A16
e usa la Window 0.1 con le stesse misure degli intervalli CPU (senza preferenza
aggiuntiva per triple buffering). Il controllo mantiene preset, branding e
correzioni di sicurezza/stabilità del JIT e della pressione memoria. Non è un
confronto tra l'app ufficiale immutata e la fork: isola gli interventi prestazionali.
Snapshot, destinazioni e SHA-256 sono in `a16/control/`. `prepare_control.py`
modifica file: usarlo soltanto nel checkout CI eliminabile.

Candidata e controllo condividono il bundle ID sperimentale e si alternano con
reinstallazione sul medesimo contenitore e stessa firma. L'originale resta a parte.

- Stesso BOTW 1.9, salvataggio, mod, impostazioni e tragitto.
- Due passaggi preliminari per riscaldare la cache di ciascuna build.
- Tre passaggi misurati di circa 60 secondi, senza menu, a temperatura simile.
- Alternare l'ordine tra controllo e candidata.
- Confrontare p95/p99, intervalli oltre 100/250 ms, memoria, difetti e blocchi.
- Senza un vantaggio ripetibile, non promuovere la candidata a stabile.

I numeri sono **intervalli fra presentazioni CPU**, non tempi GPU o del pannello.
I percentili sono bordi superiori di bucket da 1 ms; il bucket 2000 raccoglie la
fascia da 1999 ms in su. Separare caricamenti e pause dal gameplay. Non fare la
media dei percentili per ricavare un percentile globale.

## Verifiche riproducibili

```sh
dotnet build src/Ryujinx.Graphics.Vulkan/Ryujinx.Graphics.Vulkan.csproj -c Release -m:1
dotnet run --project tests/A16.Regression/A16.Regression.csproj -c Release
DOTNET_EnableAVX2=0 dotnet run --project tests/A16.Regression/A16.Regression.csproj -c Release --no-build
DOTNET_EnableHWIntrinsic=0 dotnet run --project tests/A16.Regression/A16.Regression.csproj -c Release --no-build
dotnet run --project tests/A16.Regression/A16.Regression.csproj -c Release --no-build -- --inspect-descriptors src/Ryujinx.Graphics.Vulkan/bin/Release/net10.0/Ryujinx.Graphics.Vulkan.dll
python3 -m unittest discover -s a16/tests
```

## Provenienza

- https://starforgejo.dev/projects/MeloNX
- https://starforgejo.dev/projects/MeloNX/src/commit/13700519f3abc4e6d7ed8a065b118b2cc59bcd67
- https://git.ryujinx.app/api/packages/projects/nuget/index.json
- https://github.com/actions/runner-images/blob/main/images/macos/xcode-27-arm64-Readme.md

Licenze e attribuzioni a monte restano incluse (`LICENSE.txt` e
`distribution/legal/THIRDPARTY.md`). Fork indipendente dal progetto ufficiale.
Il pacchetto non contiene giochi, firmware, chiavi o cache di giochi altrui.
