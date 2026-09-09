# Wiele peerów, kanał i porządki w API

Trzy rzeczy: dwie potrzebne, żeby aplikacja z kilkoma klientami mogła w ogóle użyć biblioteki, i lista
drobiazgów, które decydują o tym, czy ktoś zostanie przy bibliotece, czy się od niej odbije.

Przypadek napędzający: most telefoniczny mTiles — kilka telefonów na jednej maszynie, dźwięk z mikrofonu,
kod QR na urządzenie.
Plan po tamtej stronie: [`mterminal/docs/plans/PHONE-RELAY-PAIRING.md`](../../../mterminal/docs/plans/PHONE-RELAY-PAIRING.md)

---

## 1. Host z wieloma peerami

### Jak jest

`LinkState.PeerKey` to jeden klucz. `AdmitAsync` przypina pierwszego, resztę odrzuca. `PairingOffer` to
jeden token. Dobre dla przypadku z README (laptop i maszyna, do której nie masz dostępu), bez odpowiedzi
na dwóch klientów.

Dziś trzeba odpalić osobny link na klienta, z numerkiem w `appName`. To osobna tożsamość, osobny wpis w
store, osobny pomiar regionu i osobna pętla reconnect na każdego — i żadnego sposobu, żeby powiedzieć „co
najwyżej czterech". Obejście, nie API.

### Jak ma być

```csharp
await using ILinkHost host = await TailcatLink.HostManyAsync("my-app", options);

host.SetRequestHandler((peer, request, ct) => Handle(peer, request, ct));

host.PeerJoined += (s, e) => Show(e.Peer.Name, e.Peer.PairedAt);
host.PeerLeft   += (s, e) => Dim(e.Peer, e.Reason);

LinkInvitation invitation = await host.InviteAsync(new InvitationRequest
{
    Label     = "telefon w kuchni",   // dla listy operatora, nie idzie na drut
    Lifetime  = TimeSpan.FromMinutes(2),
    SingleUse = true,
});

await host.ForgetPeerAsync(peer, ct);
```

### Zasady

- **`ILink` bez zmian.** `HostAsync`/`JoinAsync` zostają i stają się fasadą jednopeerową nad tym samym —
  `HostManyAsync` z `MaxPeers = 1`. Przykład z README dalej się kompiluje.
- **`ILinkPeer` to per-peer połowa `ILink`**: `RequestAsync`, `NotifyAsync`, `SendAsync`, `IsConnected`,
  `Key`, `Name`, `PairedAt`, `LastSeen`. Handler na hoście, nie na peerze — aplikacja ma jedną logikę i
  kilku rozmówców, nie odwrotnie.
- **Zaproszenia w liczbie mnogiej.** Kilka żywych naraz, każde z własnym terminem. Dochodzi
  `host.Invitations` i `host.RevokeInvitation(id)`.
- **Joiner podaje swoją nazwę.** Dziś host wie o peerze tylko klucz publiczny, więc każda lista urządzeń
  to kolumna hexów. `JoinAsync(…, new JoinRequest { DisplayName })` → `ILinkPeer.Name`. To podpowiedź od
  drugiej maszyny, nic nią nie jest uwierzytelniane — i tak ma być napisane w doc commencie, bo pierwszą
  rzeczą, jaką zrobi czytelnik, będzie próba oparcia czegoś na tej nazwie.
- **`LinkOptions.MaxPeers`.** Host stoi na publicznym przekaźniku. To ograniczenie bezpieczeństwa, nie
  wydajnościowe — każdy wpuszczony peer wchodzi w handler aplikacji.
- **`ForgetPeerAsync`.** Dziś jest tylko `ForgetAsync(appName)`, które zabiera ze sobą tożsamość
  maszyny. „Odepnij to urządzenie" to inna operacja.
- **Wersja schematu w store + jedna migracja.** `LinkState` dostaje
  `IReadOnlyList<PairedPeer> { Key, Name, PairedAt, LastSeen }`. Stary kształt czytany raz i przepisywany.

---

## 2. Kanał — trzeci kształt między wiadomością a plikiem

### Czemu żaden z dwóch nie pasuje

Ramki czasu rzeczywistego (dźwięk, telemetria, zdarzenia wejścia). Miara: PCM 16 kHz mono 16-bit ≈ 32 kB/s
w ramkach po ~3 kB, czyli zwykły mikrofon.

| | dlaczego nie |
|---|---|
| `RequestAsync` | round-trip na ramkę, wpis w ledgerze na ramkę, limit 16 MB bez znaczenia, `Guid` na 3 kB |
| `SendAsync` | wymaga seekable, wznawia się między sesjami, tempo dyktuje odbiorca — obietnice pliku, a mikrofon plikiem nie jest |
| `NotifyAsync` | najbliżej i działa, ale to jeden `ExchangeAsync` i świeży `Guid` na ramkę, a **kolejność między wymianami nie jest nigdzie obiecana** |

### Jak ma być

```csharp
await using ILinkChannel audio = await peer.OpenChannelAsync("audio", ct);
await audio.SendAsync(frame, ct);

host.OnChannel("audio", async (peer, channel, ct) =>
{
    await foreach (ReadOnlyMemory<byte> frame in channel.ReadAllAsync(ct))
        Consume(frame.Span);
});
```

Kontrakt wprost, bo o to właśnie chodzi: **uporządkowany w obrębie kanału, nietrwały** — kanał kończy się
z sesją i nie jest wznawiany. To poprawne, nie ułomne: ramka dźwięku sprzed dziesięciu sekund jest nic
niewarta. `ChannelClosed` niesie powód, żeby dało się odróżnić „peer zamknął" od „sesja padła".

Domyka trójkę, którą czytelnik utrzyma w głowie: **wiadomość** (`RequestAsync`), **strumień chwili**
(`OpenChannelAsync`), **plik** (`SendAsync`).

**Wersja minimalna, gdyby to było za dużo:** udokumentować gwarancję kolejności, jaką `NotifyAsync`
faktycznie ma w obrębie sesji, i dodać przeciążenie omijające ledger i `Guid`.

---

## 3. Porządki w API

Nic z tego nie jest potrzebne, żeby link działał. Każde jest miejscem, gdzie ktoś czytający API pierwszy
raz musi zrobić coś, co framework zrobiłby za niego.

1. **`ILogger` zamiast `Action<string>? Log`.** Sink stringów bez poziomu, kategorii i struktury — nie da
   się tego filtrować ani nigdzie wysłać. `LoggerFactory` w `LinkOptions`, stary `Log` zostaje jako shim.
2. **Zdarzenia zgodne z wytycznymi .NET.** `event Action? Connected` i `event Action<string>? Disconnected`
   nie mają sendera ani `EventArgs`, a drugie oddaje **string** — więc każdy, kto chce inaczej zareagować
   na „peer odmówił parowania" niż na „sieć padła", dopasowuje tekst. `DisconnectedEventArgs { LinkDisconnectReason
   Reason, string Detail, Exception? Error }` z domkniętym enumem. UI pokazujący nowy QR na jedno i nic na
   drugie to przypadek zwykły, nie egzotyczny.
3. **Jeden stan zamiast dwóch flag.** `LinkConnectionState { Idle, Connecting, Connected, Reconnecting,
   Faulted }` + `StateChanged`. Dziś jest `IsConnected` i żadnego sposobu, żeby powiedzieć „próbuję".
4. **Hierarchia wyjątków.** `LinkException` na wszystko nie da się łapać selektywnie.
   `PairingRefusedException`, `InvitationExpiredException`, `LinkTimeoutException`, `LinkClosedException`.
   Klient JS **już ma** `PairingRefusedError` — czyli oba porty rozjeżdżają się na rozróżnieniu, które
   jeden z nich uznał już za konieczne.
5. **`OnRequest` nadpisuje, a czyta się jak subskrypcja.** Zawołane dwa razy po cichu gubi pierwszy
   handler. `SetRequestHandler(…)` albo właściwość `RequestHandler`.
6. **Warstwa JSON obok tekstowej.** `Tailcat.Link.Json`: `RequestAsync<TReq, TRes>`,
   `SetRequestHandler<TReq, TRes>`, z `JsonSerializerContext` pod trimming i AOT. Argument z
   `LinkTextExtensions` („JSON to jeden serializer stąd i nie należy do tej biblioteki") jest słuszny co
   do `ILink` i niesłuszny co do paczki: każdy konsument to napisze, każdy trochę inaczej.
7. **DI i hosting.** `services.AddTailcatLinkHost("my-app")`, `IHostedService` posiadający cykl życia,
   `ILinkHost` do wstrzyknięcia. Kolejność dispose'u wobec pętli nadzorczej to dokładnie ta rzecz, którą
   ludzie robią źle.
8. **`IParsable<InvitationCode>` i `ISpanFormattable`.** `Parse`/`TryParse` już są w dobrym kształcie;
   interfejsy wpuszczają typ w generyki i model binding bez adaptera. Prawie za darmo.
9. **`ActivitySource` i `Meter`** — sesje, żądania, bajty, rekonekty, przebudowy węzła. Proces chodzący z
   tym miesiąc nie ma dziś czym pokazać, co robił, a pętla nadzorcza to akurat ta część, której chce się
   mieć wykres.
10. **Wypuścić `Tailcat.TestSupport` jako paczkę.** Przekaźnik in-memory już istnieje, tylko nie da się
    go dosięgnąć spoza repo. Bez tego test cudzego handlera potrzebuje sieci.
11. **`[Experimental("TAILCAT001")]`** na powierzchni przylegającej do key schedule. README uczciwie
    mówi, że nie był recenzowany — atrybut mówi to w miejscu wywołania, a nie w dokumencie, którego nikt
    nie otwiera. Tak robi BCL.

---

## 4. Klient przeglądarkowy idzie razem

`clients/browser` odzwierciedla moduł w moduł, a `test/vectors/relay1-records.json` czyta obie strony, więc
rozjazd wywala build. Trzy rzeczy muszą wejść równolegle:

- `displayName` w `join`, zgodnie z `JoinRequest.DisplayName`
- kanał, zgodnie z `OpenChannelAsync` — ląduje w `link-frame.js` i `link-session.js`
- taksonomia błędów, żeby `PairingRefusedError` i `PairingRefusedException` znaczyły to samo

Sam multi-peer jest tylko po stronie hosta — przeglądarka i tak umie wyłącznie dołączać.

---

## 5. Kolejność

1. **Punkty 1 i 2.** Cokolwiek zbudowanego wcześniej stoi na obejściu, które potem trzeba wyjmować. Testy
   najpierw, na przekaźniku in-memory: parowanie, drugi peer, odrzucony trzeci, wygasłe zaproszenie,
   kanał ginący z sesją.
2. **Punkt 3, pozycje 1–5 i 10.** Tanie, i to na tym opiera się kod UI konsumenta. Pozycja 10 w ogóle
   umożliwia mu testowanie.
3. **Trzy rzeczy w kliencie JS.**
4. **Punkt 3, pozycje 6–9 i 11.** Nic od nich nie zależy; robią bibliotekę przyjemną.

## 6. Na co uważać

- **Fasada jednopeerowa nie może stać się drugą implementacją.** `HostAsync` to `HostManyAsync` z limitem
  jeden — inaczej obie się rozjadą, i rozjadą się w regułach parowania.
- **`ILinkPeer.Name` nie jest uwierzytelniona.** Przychodzi z drugiej maszyny.
- **Migracja pisze.** Store przepisany na nowy schemat to maszyna, której starszy build już nie odczyta.
  Zdecydować przed wypuszczeniem, czy stary build ma odmówić, czy zdegradować.
