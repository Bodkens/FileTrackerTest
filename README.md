# FileTracker

REST API služba pro sledování změn souborů v libovolné složce. Složka se předává jako parametr při každém požadavku — jedna instance služby tak může paralelně sledovat více různých složek. Každý soubor má čítač verzí, který se zvyšuje při každé změně.

## Použití AI
Primarně pouzžil jsem AI pro:
1. Vytvaření toho `README.md` :-)
2. Konzultace ohledně edge-cases a vyhledavaní slabých míst v kodu, nalezení bugů

Jínak kod jsem psal hlavně ručně a vyvmyšlel řešení samostatně. Připadné omezení: žádna validace vstupné složky, vrací to jakoukoliv složku včetně citlivé nebo systemové.

## Jak to funguje

1. Při volání endpointu `/changes?folder=<cesta>` služba naskenuje zadanou složku.
2. Soubory jsou porovnány pomocí SHA-256 hashe s předchozím uloženým stavem.
3. Výsledek (přidané, změněné, smazané soubory + verze) je vrácen v odpovědi a uložen do `Memory.json`.
4. Cesty souborů jsou ukládány jako relativní vůči sledované složce (např. `file.txt`, `subslozka/file.txt`).
5. Každá složka má vlastní semafor — souběžné požadavky na různé složky běží paralelně, na stejnou složku čekají.

## Konfigurace

V souboru `appsettings.json`:

```json
"FileTracker": {
  "MemoryFile": "./Memory.json"
}
```

| Klíč         | Popis                                          |
|--------------|------------------------------------------------|
| `MemoryFile` | Soubor, do kterého se ukládá stav (JSON)       |

## Spuštění

Požadavky: [.NET 9 SDK](https://dotnet.microsoft.com/download)

```bash
dotnet run
```

Swagger UI je dostupné na: `http://localhost:<port>/swagger`

## Endpointy

### `GET /changes?folder=<cesta>`

Spustí skenování zadané složky a vrátí změny od posledního volání spolu s aktuálními čísly verzí všech souborů.

**Parametr:**

| Název    | Typ    | Popis                          |
|----------|--------|--------------------------------|
| `folder` | string | Absolutní nebo relativní cesta ke sledované složce |

**Příklad odpovědi:**

```json
{
  "addedFiles": ["novy.txt"],
  "changedFiles": ["upraveny.txt"],
  "deletedFiles": ["smazany.txt"],
  "versions": {
    "novy.txt": 1,
    "upraveny.txt": 3,
    "subslozka/dalsi.txt": 2
  }
}
```

Pokud složka neexistuje, vrátí `404 Not Found`.
