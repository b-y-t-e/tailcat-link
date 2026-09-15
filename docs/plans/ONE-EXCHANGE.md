# Jedna wymiana, bez sztucznych limitów (0.5)

Cel: jedna metoda, która przenosi zarówno JSON o rozmiarze kilobajta, jak i film 20 GB, wznawia się po
zerwaniu połączenia i nie ma limitu rozmiaru wymyślonego przez bibliotekę. Jeśli komputerowi zabraknie
pamięci albo dysku, kod ma się wywrócić na tym, a nie na stałej.

---

## 1. Jak jest

Trzy kształty, trzy formaty na łączu:

| | na łączu | limit | po zerwaniu sesji |
|---|---|---|---|
| `RequestAsync(bytes)` | jedna `LinkFrame` | 16 MiB | wysyłana od zera, handler raz dzięki ledgerowi |
| `NotifyAsync(bytes)` | jedna `LinkFrame` | 16 MiB | **ginie**, nie ma ponowienia |
| `SendAsync(stream)` | oferta + bloki | brak | wznawia od offsetu odbiorcy, ale bez odpowiedzi z danymi |
| kanał | ramki | 256 KiB na ramkę | kończy się z sesją (i tak ma być) |

Do tego znaleziona wcześniej usterka: `SendAsync` wisi bez końca, gdy druga maszyna nie wraca, bo czekanie
na sesję nie liczy się do `TransferStallTimeout`.

## 2. Jak ma być

Jeden mechanizm na łączu dla wszystkich danych aplikacji: **wymiana** (ramka `Exchange` = 7). Każda wymiana
jest wznawialna w obie strony, ma stały identyfikator przez wszystkie sesje, handler wykonuje się raz.
Zapytanie, notyfikacja i transfer to ta sama wymiana z innymi flagami.

```csharp
// jedna metoda, każdy rozmiar
await using IncomingTransfer answer = await peer.RequestAsync(LinkContent.FromFile(@"D:\film.mp4"));
await answer.SaveToAsync(@"D:\wynik.bin");

// jeden handler, każdy rozmiar
host.SetRequestHandler(async (peer, request, ct) =>
{
    await request.SaveToAsync(Path.Combine(dir, request.SuggestedFileName), cancellationToken: ct);
    return LinkContent.FromString("ok");
});

// dotychczasowe API działa dalej, tylko bez limitu i ze wznawianiem
byte[] reply = await link.RequestAsync(json40MB);
```

Dotychczasowe metody zostają i są nakładkami na wymianę. Kod klienta, który dziś się kompiluje, kompiluje
się dalej.

---

## 3. Decyzje

### D1. Kto co potrafi: bajt możliwości w odpowiedzi na ping

**Aktualizacja: klient przeglądarkowy mówi wymian tak samo jak .NET**, ze wznawianiem w obie strony i
transferami. Tabele niżej opisują stan sprzed tej zmiany.

**Zgodność z 0.4 nie jest dla nas ważna.** Żadna maszyna 0.4 nie działa, biblioteka jest dopiero testowana.
Ścieżki dla starszych maszyn opisane niżej zostały zrobione, zanim to ustaliliśmy, i można je usunąć.

Ping (ramka 3) jest odbierany przez każdą wersję i każda ignoruje treść odpowiedzi. Nowa strona odpowiada
jednym bajtem flag:

| bit | nazwa | znaczenie |
|---|---|---|
| 0 | `LargeFrames` | przyjmuje `LinkFrame` i ramki kanału dowolnej długości |
| 1 | `Exchanges` | rozumie ramkę `Exchange` (7) |

Stara strona odpowiada pustą treścią, czyli zero. Sesja pyta raz, leniwie, przy pierwszej operacji, która
tego potrzebuje, i pamięta wynik do końca sesji. Kosztuje to jedną rundę przez przekaźnik na sesję.

Dlaczego nie w hello: stary host czyta nieznany kształt hello jako goły token i odmawia parowania. Ping
jest jedynym miejscem, które obie strony w każdej wersji już tolerują.

| nadawca → odbiorca | co jedzie |
|---|---|
| .NET 0.5 → .NET 0.5 | wymiana (7), bez limitu, wznawialna |
| .NET 0.5 → przeglądarka | stare ramki, bez limitu (przeglądarka ogłasza `LargeFrames`) |
| .NET 0.5 → .NET 0.4 | stare ramki do 16 MiB, powyżej od razu czytelny błąd |
| przeglądarka → .NET 0.5 | stare ramki, bez limitu |
| przeglądarka → .NET 0.4 | stare ramki do 16 MiB, powyżej od razu czytelny błąd |

16 MiB zostaje wyłącznie jako limit **starej wersji po drugiej stronie**, a błąd mówi dokładnie to.

### D2. Format wymiany

Jeden strumień na próbę. Ramki nagłówkowe to zwykłe `LinkFrame`, treść to bloki transferu
(`[i32 długość][bajty]`, blok zerowy kończy).

```
1. N→O  LinkFrame(7, id, nagłówek)
2. N→O  bloki treści + blok zerowy        tylko gdy flaga Pipelined (mała treść, pierwsza próba)
3. O→N  Ok([i64 offset treści]) | Failed(powód)
4. N→O  bloki treści od offsetu + blok zerowy   gdy nie Pipelined
5. O→N  wynik:
          Failed(powód)                        handler rzucił
          Ok([i64 odebrane])                   notyfikacja albo transfer
          Ok(nagłówek odpowiedzi) + bloki odpowiedzi od offsetu odpowiedzi + blok zerowy   zapytanie
6. N→O  Ok()                                   wynik odebrany (dla zapytania: cała odpowiedź), odbiorca zapomina
```

Nagłówek:

```
[u8  wersja = 1]
[u8  flagi]           bit0 Answer, bit1 Transfer, bit2 Pipelined, bit3 AckOnDelivery
[i64 długość treści]  -1 gdy nieznana
[i64 offset odpowiedzi]  ile odpowiedzi nadawca już ma; 0 za pierwszym razem
[u32 len][nazwa utf8]
[u32 len][typ utf8]
[u32 len][metadane]
```

Nagłówek odpowiedzi: `[u8 wersja = 1][i64 długość][u32 len][typ][u32 len][metadane]`.

Pola mają długość `u32`, a nie `u16` jak w ofercie transferu, bo `u16` to też limit.

**Pipelined** oszczędza rundę dla małych zapytań: nadawca, który nic jeszcze nie wysłał i ma treść znanej
długości do jednego bloku, pisze nagłówek i treść od razu, a odbiorca czyta całość przed odpowiedzią. Małe
zapytanie kosztuje wtedy jedną rundę, tak jak dziś.

**Pozycja bloku zamiast zaufania.** Odbiorca przyjmuje każdy blok z jego pozycją w treści i odrzuca bajty,
które już ma. Ta sama reguła obsługuje wznowienie, powtórzone próby i przypadek Pipelined.

### D3. Handler wykonuje się raz, najnowsza próba wygrywa

Stan wymiany u odbiorcy żyje na peerze, nie na sesji (jak dziś transfer): odebrana treść, uruchomiony
handler, jego odpowiedź. Odbiorca zapomina wymianę po potwierdzeniu z kroku 6 albo po
`TransferRetention` (10 min) od końca ostatniej próby.

Dziś druga próba tego samego transferu, gdy pierwsza jeszcze się zwija, dostaje odmowę, która zabija nową
sesję. Zamiast tego **nowa próba anuluje starą i czeka, aż ta puści**. Nadawca prowadzi jedną próbę naraz,
więc nowsza zawsze oznacza, że starsza jest porzucona.

### D4. Czas: bez terminu na całość, z limitem ciszy

Żaden rozmiar nie może mieć terminu na całość, bo termin byłby limitem rozmiaru. Każda wymiana ma jedną
**cierpliwość**: ile najdłużej nic nie może się ruszyć.

| API | cierpliwość | czekanie na handler |
|---|---|---|
| `RequestAsync(bytes)`, `NotifyAsync(bytes)` | `RequestDeadline` | w cierpliwości |
| `RequestAsync(LinkContent)` | `TransferStallTimeout` | w cierpliwości |
| `NotifyAsync(LinkContent)` | `TransferStallTimeout` | nie czeka, potwierdzenie przy dostarczeniu |
| `SendAsync` | `TransferStallTimeout` | do końca handlera, póki sesja żyje (jak dziś) |

Ruch to każdy blok w którąkolwiek stronę. **Czekanie na nową sesję liczy się do cierpliwości**, co naprawia
wieszanie się `SendAsync`.

Małe zapytanie do maszyny offline dalej kończy się po `RequestDeadline`, jak dziś. Zapytanie 2 GB, które się
przesuwa, nie kończy się nigdy.

Własny czytelnik odpowiedzi, który przestał czytać, nie jest ciszą sieci i nie zabija sesji: limit ciszy
obejmuje tylko odczyt z sieci, nie czekanie na miejsce w buforze aplikacji.

### D5. Pamięć rośnie z danymi, nie z zapowiedzi

Każde czytanie ramki bez limitu alokuje tyle, ile naprawdę przyszło, podwajając bufor aż do zapowiedzianej
długości. Zapowiedź 2 GB i trzy bajty to trzy bajty pamięci, a nie wywrotka.

Treść strumieniowa nie leży w pamięci wcale: bufor między siecią a handlerem to jak dziś 4 MiB, reszta to
backpressure. Nakładki na bajty (`byte[]`, `string`) czytają całość do pamięci i wywracają się, gdy się nie
mieści, zgodnie z założeniem.

### D6. Co zostaje ograniczone i dlaczego

Tylko rzeczy, które nie są danymi aplikacji:

| limit | dlaczego zostaje |
|---|---|
| ramka hello przed sparowaniem: 4 KiB | czyta ją host, zanim wie, kto dzwoni; adres hosta widzi każdy na przekaźniku |
| odpowiedź na hello u dzwoniącego: 4 KiB | ta sama chwila po drugiej stronie |
| nazwa urządzenia: 256 bajtów | część hello, zapisywana na dysk hosta |
| nazwa kanału: 256 bajtów | identyfikator, nie treść |
| blok 256 KiB, rekord relay1 32 KiB | wielkość kawałka, nie limit całości |
| ledger 4096 równoległych zapytań | tylko dla starych peerów; wymiana go nie ma |

Znikają: 16 MiB wiadomości, 256 KiB ramki kanału, 1 KiB nazwy i typu oraz 64 KiB metadanych (w nowym
nagłówku; stara oferta transferu do starych peerów zachowuje stare pola, bo tak czyta ją stary odbiorca).

### D7. Publiczne API

Nowe:

- `LinkContent` — co wysyłamy: `FromBytes`, `FromString`, `FromStream(stream, leaveOpen)`, `FromFile`,
  `Empty`, oraz `Name`, `ContentType`, `Metadata`, `Length`.
- `ILink`/`ILinkPeer.RequestAsync(LinkContent, progress, ct)` → `IncomingTransfer` z odpowiedzią.
- `ILink`/`ILinkPeer.NotifyAsync(LinkContent, progress, ct)`.
- `LinkContentHandler`, `LinkPeerContentHandler`, oraz przeciążenia `OnRequest`/`SetRequestHandler`.
- `IncomingTransfer.ReadAllBytesAsync`, `ReadAllTextAsync`, oraz `IAsyncDisposable`: zwolnienie odpowiedzi
  przed końcem ją porzuca.

Zostają bez zmian sygnatur: `RequestAsync(bytes)`, `NotifyAsync(bytes)`, `SendAsync`, `OnTransfer`,
`SetTransferHandler`, rozszerzenia tekstowe, `Tailcat.Link.Json`.

Jeden slot handlera zapytań: handler na bajtach jest opakowaniem handlera na treści, więc ustawienie
jednego zastępuje drugi. Slot transferów zostaje osobny, bo aplikacje mają dziś oba naraz
(`SaveTransfersTo` obok `OnRequest`). Flaga `Transfer` w nagłówku wybiera slot.

**Odpowiedź strumieniowa.** `RequestAsync(LinkContent)` zwraca, gdy przyjdzie nagłówek odpowiedzi, a nie
cała odpowiedź. Reszta dopływa w tle przez kolejne sesje. Token wywołania obejmuje całą wymianę, łącznie ze
ściąganiem odpowiedzi.

**Zasada przewijania.** Wznowienie wymaga treści, którą da się przewinąć: pliku, tablicy, strumienia z
`CanSeek`. To dotyczy także odpowiedzi zwróconej przez handler. Strumień, którego nie da się przewinąć,
działa do pierwszego zerwania, a po nim kończy się czytelnym błędem, nie cichą dziurą w danych.

### D8. Przeglądarka

Klient JS dostaje w tym wydaniu `LargeFrames`: odpowiada na ping bajtem możliwości, czyta możliwości hosta,
czyta ramki i ramki kanału bez limitu, a do starego hosta pilnuje 16 MiB i 256 KiB z czytelnym błędem.
Wymiany (7) nie mówi, tak jak dziś nie mówi transferów, więc duże dane do i z przeglądarki nie wznawiają
się po zerwaniu. To jest następny krok, nie ten.

---

## 4. Kolejność prac

1. Możliwości w pingu (.NET i JS), wektory.
2. Czytanie ramek bez limitu z rosnącym buforem, limit hello, sprawdzenia wobec starych peerów (.NET i JS),
   ramki kanału.
3. `ExchangeFrame`: nagłówki, flagi, wektory, testy formatu.
4. `IncomingTransfer`: bloki z pozycją, zwalnianie, czytanie całości. `IncomingExchange` i
   `ExchangeRegistry` u odbiorcy zamiast `TransferRegistry`, obsługa starych transferów tym samym kodem.
5. `OutboundExchange` i próba wymiany w `LinkSession`, obsługa ramki 7.
6. Pętla w `LinkPeer`: cierpliwość, ograniczone czekanie na sesję, odpowiedź w tle, ścieżki do starych
   peerów.
7. Publiczne API, fasada, host, rozszerzenia.
8. Testy.
9. Dokumentacja: `docs/exchanges.md` jako specyfikacja, `transfers.md`, `channels.md`, `relay1.md`, oba
   README, `CLAUDE.md`.

## 5. Testy

- zapytanie 20 MB w obie strony, po QUIC i po relay1
- zerwanie w połowie treści: dane całe, handler raz, treść czytana z dysku mniej niż półtora raza
- zerwanie w połowie odpowiedzi: to samo dla odpowiedzi
- sesja pada po handlerze, przed odebraniem odpowiedzi: handler raz
- notyfikacja przeżywa zerwanie i wykonuje się raz (dziś ginie)
- druga maszyna znika na dobre: `LinkTimeoutException` po cierpliwości, także dla `SendAsync`
- treść i odpowiedź bez przewijania: działa bez zerwania, po zerwaniu czytelny błąd, nie wisi
- porzucona odpowiedź: nadawca przestaje, odbiorca nie wisi, handler raz
- stary peer (wymuszone możliwości = 0): małe działa, 17 MiB od razu czytelny błąd
- przeglądarka (możliwości = `LargeFrames`): stare ramki powyżej 16 MiB przechodzą
- obcy zapowiadający ogromne hello: odrzucony, host żyje, bez dużej alokacji
- zapowiedź długości bez danych: brak alokacji na zapowiedź
- ramka kanału 1 MiB (.NET i JS)
- wektory: bajt możliwości, nagłówek wymiany, nagłówek odpowiedzi
- `DisposingTwiceIsHarmless` dla odpowiedzi
