# MeloNX A16 Lab 0.2 — candidata sperimentale

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

`a16/validation/` contiene verbale e log della compilazione gestita e dei test.
I test delle code usano il file di produzione, sostituendo il driver con un
simulatore controllabile. Non sono benchmark dell'emulazione.

La compilazione C# gestita non è una build iOS. La CI verifica sintassi Swift
ed esegue il type-check del solo ponte hardware contro l'SDK iOS, senza linkare.
**Compilazione completa Swift/NativeAOT iOS, firma, installazione, JIT,
estensione RAM e BOTW sul dispositivo restano da verificare**.
Non sono dichiarati incrementi di FPS o riduzioni percentuali degli scatti.

## Risoluzione e impostazioni

Handheld 1× corrisponde normalmente al target 720p; la risoluzione dinamica
interna del gioco rimane indipendente. Questa fork non certifica un minimo
effettivo 1280×720. L'eventuale mod DRS deve essere identica fra le prove.
Non è stata aggiunta una patch FPS al gioco né corretto il precedente cap a 25.

Async compilation può far comparire nuovi effetti in ritardo, causare difetti
grafici o crash: si può disattivare nelle impostazioni del titolo. Il profilo non
modifica le opzioni di donazione RAM/GPU. Firma, entitlement memoria e JIT restano
necessari secondo il metodo d'installazione. La RAM fisica non aumenta.

L'app si chiama **MeloNX A16 Lab**, ID `dev.a16lab.MeloNX`, per affiancare
l'originale. Salvataggi, cache e impostazioni appartengono a un contenitore
separato e non vengono trasferiti automaticamente: conservarne una copia.

## Build cloud senza un Mac personale

**Generazione IPA sospesa su richiesta dell'utente. Non è stata avviata.**
Le seguenti istruzioni servono alla fase successiva, dopo le altre modifiche:


1. Aprire **Actions → Build A16 unsigned IPA → Run workflow**.
2. Selezionare `optimized`.
3. Quando il job termina con successo, scaricare l'artifact
   `MeloNX-A16-Lab-optimized-unsigned`.
4. L'artifact conterrà IPA e SHA-256 **solo se la compilazione riesce**.
5. Firmare con il metodo già funzionante, mantenere entitlement memoria ed
   estensione RAM e abilitare JIT con StikDebug.

Il workflow usa runner `xcode-27`, Xcode 27.1 e .NET 10.0.403. SDK 27 è necessario
per API UIKit 27 già presenti nel sorgente originale. Non contiene credenziali.
Il workflow è predisposto ma non ancora eseguito: possono emergere ulteriori
problemi di compilazione o firma. Su Mac: `bash a16/build_ipa.sh`.

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
