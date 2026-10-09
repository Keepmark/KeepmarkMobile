# Keepmark — Regole di design, stili e comportamento

Documento normativo per chi estende l'app. Le regole qui descritte sono
ricavate dal codice esistente (`src/KeepmarkMobile`, `wwwroot/app.css`) e vanno
seguite per ogni nuova funzionalità. Eccezioni solo con motivazione esplicita.

---

## 1. Le icone del notch / status bar sono SEMPRE visibili

Questa è la regola n. 1, non negoziabile: **ora, segnale, batteria e icone di
sistema non devono mai essere coperti o toccati dal contenuto dell'app,
nemmeno durante lo scroll**.

È garantita su tre livelli, che devono restare tutti attivi:

1. **Finestra Android non edge-to-edge** — `Platforms/Android/MainActivity.cs`
   chiama `WindowCompat.SetDecorFitsSystemWindows(Window, true)`: la WebView
   inizia sotto la status bar per costruzione, nessun disegno sotto il notch.
   Non riattivare l'edge-to-edge.
2. **Rispetto delle safe-area in CSS** — ogni contenitore fisso usa
   `env(safe-area-inset-*)` con fallback:
   `padding-top: max(1rem, env(safe-area-inset-top, 0px))`,
   `padding-bottom: max(1rem, env(safe-area-inset-bottom, 0px))`,
   laterali con `max(..., env(safe-area-inset-left/right))`.
   Lo spacer `.status-bar-safe-area` (`index.html`, `app.css:112`) tiene
   libero lo spazio di sistema sopra la top bar.
3. **Scroll confinato, mai del contenitore radice** — ogni area scrollabile
   è un contenitore interno dedicato (`overflow-y: auto` +
   `overscroll-behavior: contain`); il contenitore esterno è fisso con
   `overflow: hidden`. Lo scroll non deve mai propagarsi alla pagina dietro
   (scroll-chaining) né far risalire contenuto verso il notch.

### Regole per nuove schermate / overlay

- Un nuovo overlay full-screen deve copiare il pattern `.xbs-tree-full`
  (`app.css`): `position: fixed; inset: 0; display: flex;
  flex-direction: column; overflow: hidden`, con header e footer come flex
  item fissi (`flex-shrink: 0`) e **una sola** zona di scroll interna.
- Barre di ricerca/filtri negli overlay: elemento fisso in alto, **mai**
  `position: sticky` con offset pensati per la pagina principale
  (es. `top: 3rem` vale solo sotto la top bar della Home).
- Sheet modali: backdrop con `touch-action: none` + `overscroll-behavior:
  contain`; sheet interno `touch-action: pan-y`; blocco scroll del documento
  via `xbsGestures.lockScroll(on, owner)` con chiave proprietaria
  (`"sheet"`, `"nav"`) e rilascio in `Dispose`.
- Verifica manuale obbligatoria: apri la schermata, scorri fino in fondo in
  entrambe le direzioni, ruota il dispositivo. Le icone di sistema restano
  sempre intatte e nessun contenuto passa sopra di esse.

---

## 2. Token di marca e colori

Definiti in `:root` (`app.css:9-16`):

| Token | Valore | Uso |
|---|---|---|
| `--km-navy` | `#172552` | App bar, status bar, lock overlay |
| `--km-navy-deep` | `#0e1430` | Varianti scure navy |
| `--km-accent` | `#4a5fc1` | Accenti secondari |
| `--km-danger` | `#b32121` | Pericolo, errori, underlay distruttivo |
| `--km-gold` | `#b58900` | Pin, sezione pinned |
| `--km-green` | `#1f6f5c` | Cartelle, underlay sinistro, toast ok |

Altri colori d'uso (non ridefinire a caso):

- Blu primario azioni/selezione: `#1b6ec2` (bottoni, sort attivo, flash,
  highlight selezione `rgba(27,110,194,0.15)`).
- Testo secondario: `var(--bs-secondary-color, #6c757d)` — sottotitoli,
  path, conteggi, chevron.
- Testo su bottoni custom: **sempre** `color: var(--bs-body-color, #212529)`
  esplicito. Mai fidarsi del colore UA del browser: con `color-scheme: auto`
  segue il tema OS e diventa bianco-su-bianco in light mode
  (vedi commenti su `.xbs-crumb`, `.xbs-sort`).

### Tema chiaro / scuro

- Il tema (`System/Light/Dark` da `XbsSettings`) si applica su due canali
  insieme: DOM via `xbsSetTheme()` (`index.html`, `data-bs-theme` + listener
  live su `prefers-color-scheme`) e chrome OS via
  `IStatusBarService.ApplyTheme()` — su startup, su Save impostazioni e su
  `OnResume`.
- Bootstrap gestisce i propri componenti da solo; in `app.css` personalizzare
  solo i custom hardcoded-light (sezione dark in fondo al file).
- `html, body` dichiara `color-scheme: light dark`; `#blazor-error-ui` resta
  `light only`.

---

## 3. Layout e spaziature

- Font: `'Helvetica Neue', Helvetica, Arial, sans-serif`. `overflow-x: clip`,
  `max-width: 100vw` su `html, body`: niente scroll orizzontale di pagina.
- `.content`: padding-top che libera la top bar fissa
  (`3rem + safe-area-top + 0.15rem`), padding-bottom che libera la status bar
  fissa (`3.5rem + safe-area-bottom`), laterali con safe-area.
- Top bar sottile fissa (~3rem); `.xbs-appbar` brand: logo 28px + nome
  bold 1.05rem.
- Tablet/larghi (`min-width: 900px`): contenuto a `max-width: 760px`
  centrato; il drawer resta overlay, non diventa sidebar.
- Raggio angoli: card/righe `0.5rem`, bottoni piccoli `0.375rem`,
  crumbs pill `1.5rem`, FAB circolare, sheet `1rem` solo sopra.
- Spaziatura verticale liste: `margin-bottom: 0.5rem` tra righe.

---

## 4. Componenti: aspetto e comportamento

### Righe bookmark/cartella (`.xbs-row`)

- Card full-width a griglia `1fr 48px` (testo + chevron/azioni), bordo
  `1px var(--bs-border-color)`, sfondo `var(--bs-body-bg)`.
- Nastro d'angolo (`.xbs-ribbon`, 10×16px, overlay senza spazio di layout):
  blu = bookmark, verde = cartella, grigio = separatore; il testo ha
  `padding-left: 14px` per non finirci sotto.
- Titolo `600`, una riga con ellipsis; sottotitolo `0.85rem` secondario;
  path `0.75rem` secondario opaco. Swipe-destra (expand) mostra la versione
  estesa: testo a capo con `overflow-wrap: anywhere` + descrizione e tag
  (tag in blu `#1b6ec2`).
- Separatori: bordo tratteggiato, `opacity: 0.7`, `aria-hidden`, esclusi da
  gesture e assistive technology.
- Sezione pinned: box con bordo oro `2px`, sfondo oro al 7%, header
  collassabile `48px` con chevron.

### Ricerca + briciole + ordinamento (stack sticky della Home)

- Ordine fisso: search sticky → crumbs sticky → sortbar sticky. Ciascuna con
  sfondo pieno del body e `z-index` decrescente (950 / 900 / 890), così le
  righe che scorrono sotto non traspaiono mai.
- Search: lente a sinistra, padding simmetrico `2.5rem`, altezza `48px`.
- Crumbs: scroll orizzontale senza scrollbar visibile, pill `48px` con
  `max-width: 180px` ed ellipsis.
- Sortbar: sticky in fondo allo stack (`position: sticky;
  top: calc(3rem + env(safe-area-inset-top, 0px) + 3.2rem + 3.6rem)` —
  top bar + search + crumbs, `z-index: 890`, sfondo pieno del body); solo
  display (`Default/Asc/Desc`), mai persistita sul blob; bottone attivo blu
  con testo bianco + `aria-pressed`.

### FAB, status bar, toast, undo

- FAB `+`: cerchio 60px, fisso sopra la status bar, ombra, `opacity: 0.85`.
- Status bar inferiore (`.xbs-statusbar`): fissa, `44px` (contiene target da
  44px), testo
  `0.8rem` secondario con ellipsis; pill di sync solo in modalità Manuale.
- Toast: centrato, `max 480px`, bordo-sinistro spesso (blu ok / rosso
  errore), icona `✓/⚠`, `role="status"`, auto-dismiss 3.5s, sempre
  fire-and-forget dopo `StateHasChanged`, mai bloccante.
- Undo bar: fissa sopra la status bar, scura, visibile 6s dopo delete
  ottimistica (la UI rimuove subito, il DB segue; undo ripristina).

### Sheet editor (`.xbs-sheet`)

- Bottom sheet: backdrop `rgba(0,0,0,0.45)` che chiude al tap, card fino a
  `88dvh` con angoli superiori arrotondati.
- Snapshot anti-wipe: i re-render da sync in background non cancellano mai
  il digitato (chiave `openKey`, ritorno precoce se invariata).
- Discard-confirm solo se dirty (6 campi vs snapshot); validazione con
  `fieldError` + `role="alert"`; URL solo `http/https` assoluti.
- Toggle Bookmark/Cartella non distruttivo: i campi nascosti si conservano.

### Albero cartelle (`FolderPicker`)

- Riga = gutter expander fisso (32px, placeholder invisibile per le foglie)
  + bottone cartella: le icone sono allineate tra fratelli con/senza figli.
- Indent lineare: ogni livello aggiunge `1.5rem` (`xbs-tree-kids`), mai
  margini cumulativi per livello.
- Highlight di selezione sulla **riga intera** (expander incluso), non sul
  solo bottone.
- Default tutto collassato, path verso la selezione riaperto; mai spostare
  una cartella dentro sé stessa (`ExcludeSubtreeRoot`).
- Chevron `›` ruota di 90° da aperto (`.open`); filtro `oninput` diretto con
  conteggio `role="status"` ed empty-state dedicato.

### Sync, lock, log

- Sync: toploader indeterminato in alto + pill scura `Syncing…`; toast con
  orario al termine manuale.
- App lock: overlay navy full-screen (`z-index: 2000`, sopra tutto) con card
  centrata; blocco immediato se timeout 0, altrimenti su `OnResume` oltre
  soglia; sblocco con PIN/biometria di sistema.
- Log: filtro livello + testo `oninput` immediato sugli ultimi 300;
  esportazione via condivisione file di sistema.

---

## 5. Touch target e feedback

- Altezza minima tocchi: **44px** (righe, bottoni underlay, chevron,
  sort, log). Casi a **48px**: griglia riga, search, crumbs, header pinned.
- Frecce vai-su/giù: grafica 30px ma hit-area 44×44px.
- Feedback obbligatorio: `.xbs-tapped` al tocco sulle righe, `.selected`
  per selezioni, `.active` per sort, animazione `xbs-flash` (3 impulsi blu)
  quando si salta a una riga, rotazione chevron su expand/collapse,
  badge con conteggio dove c'è una coda (Duplicates, `Delete marked (N)`).

---

## 6. Accessibilità (ARIA)

- Routing: il focus va sull'`h1` a ogni navigazione (`FocusOnNavigate`).
- Dialog: `role="dialog" aria-modal="true"` (sheet editor, action-sheet,
  lock); backdrop con `stopPropagation`.
- Alberi: `role="tree"` + `treeitem` con `aria-selected`, `aria-level`,
  `aria-expanded` solo se con figli; bottoni expand con label
  `Expand/Collapse …`; decorazioni sempre `aria-hidden`.
- Liste: righe `role="button" tabindex="0"`, solo `Enter` attiva
  (lo Spazio scrollerebbe la pagina); `aria-label` su ogni azione iconica.
- Eccezione `aria-expanded`: le cartelle di BookmarkRow sono navigazione
  (il tap apre la cartella), non disclosure — niente `aria-expanded` lì.
  L'espansione si dichiara solo dove c'è expand/collapse reale (FolderPicker,
  sezione pinned).
- Errori in `role="alert"`, conteggi/stati in `role="status"`, sync in
  `role="progressbar"`, `aria-invalid` + `aria-describedby` sui campi.
- Contrasto: testo secondario solo per info non essenziali; mai solo-colore
  per stati critici (toast ha anche icona e testo).

---

## 7. Comportamenti trasversali

- **Gesture** (`gestures.js`, delega su `document`): tap = apri, swipe-destra
  = espandi, swipe-sinistra = comprimi, long-press 500ms = menu. Soglie:
  12px con rapporto 1.5× per iniziare lo swipe, 60px per confermarlo; swipe
  e long-press sopprimono il tap successivo (400ms). Separatore e bottoni
  interni sono ignorati dalle gesture.
- **Ricerca store**: debounce 250ms + contatore di generazione (scarta i
  risultati stale), soglia 2 caratteri, 50 risultati. Filtri locali piccoli
  (albero, log): `oninput` diretto senza debounce.
- **Conferme distruttive**: booleano `armed*` della pagina + `ConfirmBlock`
  (`Components/ConfirmBlock.razor`) adiacente all'azione, testi da
  `WorkflowTexts` (uguali all'estensione, `docs/WORKFLOWS.md`); la
  validazione gira prima di armare; ogni cluster ha il suo box errore vicino.
- **Password**: mai persistite; ogni step azzera i segreti che ha consumato
  quando riesce, mai su errore (servono al retry); minimo 8 caratteri per
  ogni password nuova (`WorkflowRules`).
- **Stabilità dati**: i `RowId` cambiano a ogni sync — rifare il rebase su
  `NodeId` stabile (crumbs, expanded, pinned) prima di ridisegnare.

---

## 8. Convenzioni di codice UI

- Classi `xbs-*` = tutto il custom comportamentale (righe, sheet, albero,
  search, toast, lock, gesture). Bootstrap = solo form e chrome generico
  (`btn`, `form-control`, `d-flex`, `alert`, `badge`, `spinner`).
- Scala z-index: sort/dupbar 890 < crumbs 900 < search 950 < error-ui 1000
  < spacer status 1050 < statusbar 1070 < FAB = frecce go 1080 < undo 1090
  < sheet 1100 < tree-full 1150 < toast 1200 < sync-loader 1300 < lock 2000.
  Nuovi livelli si inseriscono nella scala, non sopra il lock (tranne errori
  fatali). Le frecce go condividono il livello del FAB (1080) ma non l'angolo:
  riusano un unico slot sopra il FAB (FAB 60px a bottom 4.5rem, slot a +72px
  con gap 12px, centrato sull'asse +8px); ↑ e ↓ non sono mai visibili insieme.
  Così non coprono mai FAB, sortbar né statusbar (sono transienti con
  auto-hide 2s); l'undo (1090) dipinge sopra in caso di coincidenza.
- Componenti presentazionali: zero fetch diretti, dati dal parent via
  `[Parameter]`, eventi al parent via `EventCallback`; logica di espansione
  e filtro resta locale al componente.

---

## 9. Checklist per una nuova funzionalità

1. Le icone del notch restano visibili in ogni stato e orientamento?
2. Lo scroll è confinato in un contenitore interno con `overscroll-behavior:
   contain`? Il modale blocca lo scroll dietro con chiave proprietaria?
3. Touch target ≥ 44px? Feedback visivo (tap/selected/active) presente?
4. Safe-area rispettate (top/bottom/lati) con fallback?
5. ARIA: ruoli, label, focus, stati, alert/status dove servono?
6. Tema dark verificato (niente bianco-su-bianco, niente colori UA)?
7. Toast fire-and-forget, mai bloccanti; errori vicino all'azione?
8. Password azzerate, conferme distruttive armate, dirty-check dove si edita?
9. Solo classi `xbs-*` per il custom, Bootstrap per form/chrome?
10. Build Android verde + prova su dispositivo prima del commit.
