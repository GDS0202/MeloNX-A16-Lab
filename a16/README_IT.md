# MeloNX A16 Lab 0.1 — candidata sperimentale

Fork del sorgente completo ufficiale **MeloNX 2.7**, commit
`13700519f3abc4e6d7ed8a065b118b2cc59bcd67` (tag annotato
`e91d806742874ec2a98835a23a2f85b668760dba`). Obiettivo: ridurre attese e
irregolarità dei frame su iPad A16 mantenendo scala 1×. Non sono ancora stati
misurati miglioramenti sull'iPad e non è una release stabile.

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

La compilazione C# gestita non è una build iOS. **Swift, NativeAOT iOS, firma,
installazione, JIT, estensione RAM e BOTW sul dispositivo restano da verificare**.
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

Dopo la pubblicazione di questa repository su GitHub:

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

Il workflow offre `control`: ripristina esattamente scheduler e gestore cache
della 2.7, mantenendo uguali profilo e misure. I due sorgenti originali e SHA-256
sono in `a16/control/`. `prepare_control.py` modifica file: usarlo soltanto nel
checkout CI eliminabile.

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
