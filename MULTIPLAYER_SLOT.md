# Multiplayer (co-op) — design & status

**Status: IMPLEMENTED (builds green, deployed). One remaining gate — an actual 2-client co-op session
(see Verification plan). SP unchanged.**

Implemented across: `SlotNet.cs` (seam + transport), `SlotNetConsoleCmd.cs` (the `slot_sync` wire),
`SlotToast.cs` (the "partner took it" banner), plus edits to `MerchantSlotCabinet.cs` (gate removed +
shop broadcast), `SlotMachineState.cs` (union pool, `PPool` outcome, party-wide jackpot), and
`SlotMachinePopup.cs` (payout sync, peer-shop grant + deplete, pool meter/paytable/win).

### Decisions finalized during implementation
- **Shared pool is always on** (incl. single-player, where it degrades to a personal progressive pot fed
  by your own bets). `PPool = 5.0%`. Every full 20-gold bet feeds the pool, so over time the pool returns
  roughly all bets — the machine is more generous in this mode by design (it's a "fun", non-balance mod).
  Tuning knobs if wanted: `SlotMachineState.PPool`, or feed a fraction of the bet in `OnSpin`.
- **Pool win display is optimistic in co-op.** `AddToPool`/`WinPool` ride the ordered queue, so the
  *granted* amount is exact (handler reads the post-add pool), but the *displayed* number in the win
  toast is read locally at resolve time and can lag by ~1 bet / a peer's in-flight bet. The actual gold
  (via `GoldChanged`) is correct.
- **Pool is not serialized** — a static field for the live session. Save/load or a mid-run join resets it
  to 0 (same class of limitation as RelicForge's mid-join note).
- **Manual mode can't win the pool** (no pool symbol lands), so the pool row is hidden from the manual
  paytable, though manual bets still feed it.
- **Deplete UI refresh** uses the game's own path: reflection-invoke the protected `ClearAfterPurchase`
  (sets `Model = null`), then the PUBLIC `OnMerchantInventoryUpdated()` which raises `EntryUpdated` → the
  bound `NMerchantRelicSlot.UpdateVisual` hides the empty slot. Only one reflection call.

---

## (original design record below)

**Status: DESIGN LOCKED, implementation in progress.**

Lucky Relic Reels was single-player only (the cabinet was gated off when `Players.Count > 1`). This
document is the design record for co-op support, plus a set of **co-op-only interactions** that only
make sense with two players sharing a merchant room.

Prior art: `../Sts2RelicForge/MULTIPLAYER_REFORGE.md` — same net transport (reuse the built-in
`ConsoleCmdGameAction` wire so the mod adds no new `INetAction` type; lockstep-safe as long as both
clients run the same mod version).

---

## Key facts established from the decompiled game (`research/sts2_decomp/sts2.decompiled.cs`)

1. **The merchant shop is PER-PLAYER, not shared.** `MerchantRoom.EnterInternal` builds the inventory
   only for `LocalContext.GetMe(runState)` (line ~40374); `MerchantInventory` holds a single
   `Player Player` and each `MerchantRelicEntry` rolls from `_player.PlayerRng.Shops`. `NMerchantRoom`
   takes the full players list only for visuals (`PlayerVisuals`). So each co-op player sees & buys
   their OWN independently-rolled stock. **There is no shared stock to keep in sync** — which is why
   the earlier "shared-shop relic competition" worry (old memory note) does not apply.

2. **Both players are in the SAME merchant room at the same time** (shared map/room), each with their
   own inventory. So a "linked machines" fantasy works live: both stand in the shop, two cabinets,
   each fed by its own stock, and wins can cross between the two stocks in real time.

3. **`PlayerCmd.GainGold/LoseGold` and `RelicCmd.Obtain` are LOCAL-ONLY** — they mutate local state and
   do NOT auto-replicate. Out-of-combat economy/reward replication is a SEPARATE explicit step:
   `RunManager.Instance.RewardSynchronizer.SyncLocal*(...)` sends a message; the peer resolves the
   **sender's** `Player` by senderId and re-runs the same `Cmd` locally. The vanilla shop purchase
   (`MerchantRelicEntry.OnTryPurchase`, line ~385889) is the template:
   ```csharp
   if (!ignoreCost) await PlayerCmd.LoseGold(Cost, _player, GoldLossType.Spent);   // local
   await RelicCmd.Obtain(Model, _player);                                          // local
   RunManager.Instance.RewardSynchronizer.SyncLocalGoldLost(Cost);                 // → peer
   RunManager.Instance.RewardSynchronizer.SyncLocalObtainedRelic(Model);           // → peer
   ```
   **Every `SyncLocal*` THROWS if called during combat** (`IsInProgress && !singleplayer`). A shop is
   out of combat, so this is fine — but never call them mid-combat (the `slot` test command can open
   the machine anywhere, so guard on `!CombatManager.Instance.IsInProgress`).

   > **Gotcha (verify in the 2-client test).** `OnTryPurchase` calls `SyncLocalGoldLost(Cost)`
   > *unconditionally*, even under `ignoreCost:true`. So a **free** shop-relic grant still tells the
   > peer the roller lost `Cost` gold. Out of combat this is likely cosmetic / self-healing (gold is
   > not combat-checksummed), but if the peer's mirror of the roller's gold visibly drifts on relic
   > wins, compensate with `SyncLocalObtainedGold(Cost)` right after the free purchase.

4. **`ConsoleCmdGameAction` works out of combat.** RelicForge enqueues
   `RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(owner, str, inCombat:false))`
   at rest sites / shops. The action replays the command string through `DevConsole.ProcessNetCommand`
   on **every** client (including the initiator), in a deterministic order. `issuingPlayer` in the
   command's `Process` is the action's owner, resolved per-client by NetId. This is our general wire
   for anything that isn't a plain gold/relic reward.

5. **Removing a relic from a shop without granting it** = `MerchantEntry.ClearAfterPurchase()` sets the
   relic entry's `Model = null` (line ~385902), but it is `protected abstract` — no public entry point.
   So the deplete path needs **reflection** to clear the entry + a refresh of the shop's `NMerchantRelicSlot`
   UI. This is the one fragile piece; prototype it first.

---

## The co-op feature set (locked)

### 0. Base wiring (prerequisite)
- Remove the `Players.Count > 1` gate in `MerchantSlotCabinetPatch`. Attach the cabinet on every
  client — each player gets their own cabinet + own per-player shop.
- Each spin's mutations now fire the matching `SyncLocal*` so the peer's mirror stays correct:
  - bet: `LoseGold(bet)` + `SyncLocalGoldLost(bet)`
  - gold win: `GainGold(gold)` + `SyncLocalObtainedGold(gold)`
  - jackpot relic: `RelicCmd.Obtain` + `SyncLocalObtainedRelic`
  - own-shop relic: `OnTryPurchaseWrapper` already syncs its relic (see §3 gotcha for gold).
- Helper `SlotNet.SyncReward(Action)`: no-op in single-player/fake-MP; skip if `IsInProgress`
  (SyncLocal* would throw); try/catch/log.

### 1. Union reel pool — win from EITHER merchant's stock
Each player's reels can win relics that are for sale in **the other player's** shop too. Since the peer's
inventory does not exist on the local client (fact 1) and rolling it locally would corrupt the peer's
RNG, the peer must **broadcast** its shop's relic ids:
- On shop open (and after a restock), each client broadcasts its shop's relic entry ids:
  `slot_shop <id> <id> ...` (owner = the local player). The peer caches the sender's list keyed by
  sender NetId.
- `SlotMachineState` merges the cached peer list into the winnable pool as **`IsPeerShop`** symbols
  (has a `RelicModel` + id, but no local `ShopEntry`). Icons/models are shared data, so they render
  locally without any extra transfer.
- Grant on a peer-shop win: `RelicCmd.Obtain(model.ToMutable(), me)` + `SyncLocalObtainedRelic(model)`
  (NOT `OnTryPurchaseWrapper`, which needs a local entry) — **plus** the deplete message (§2).

### 2. Deplete — winning a peer-shop relic removes it from their shop
"If I win it on the slot, it vanishes from your merchant." The two players are in the same room, so both
inventories are live.
- On a peer-shop win, after granting, broadcast `slot_take <relicEntry>` (owner = the taker).
- Handler on every client: **peers only** (`!LocalContext.IsMe(issuingPlayer)`) look in their OWN
  shop for an entry whose model id == `relicEntry` and clear it (reflection `ClearAfterPurchase`) +
  refresh its `NMerchantRelicSlot` UI + show a toast: "동료가 [X]를 슬롯으로 가져갔습니다!"
- **Optimistic / deplete-if-present:** if the relic is already gone (the owner bought it first, or a
  race), the clear is a no-op; the taker keeps the relic. No refund, no hard desync (shop stock is
  per-client derived UI state, not combat-checksummed).

### 3. Party-wide one-time jackpot relic (SignetRing)
Today the jackpot relic is one-time **per player** (`PlayerOwnsJackpot` checks only `_player.Relics`).
Make it one-time for the **whole party**: once ANY player obtains SignetRing, it drops from everyone's
reels. Cheap, because relic obtains already replicate (fact 3) — the peer's copy of the winner's
`Player.Relics` gains SignetRing automatically.
- `PlayerOwnsJackpot` scans `RunManager.Instance.State.Players` (any owner), not just `_player`.
- Trigger a reel `Refresh()` when a jackpot is claimed so the symbol drops immediately (broadcast a
  tiny `slot_jackpotclaimed` nudge, or just Refresh on popup open / shop entry — jackpot is 0.2% rare).

### 4. Shared gold pool — a run-long progressive jackpot
A single pot both players feed, won on ~5% of spins, winner-takes-all, then resets.
- **Feed:** every spin's bet (20g) is added to the shared pool. The pool persists across shops for the
  whole run (not per-shop).
- **Win:** a new `PPool ≈ 50‰ (5%)` outcome in `SlotMachineState.Spin()`. On a pool hit the roller wins
  the **entire current pool**, then it resets to 0.
- **Sync:** the pool is a synced integer kept identical on all clients via the ordered action queue
  (fact 4):
  - `slot_pool add <amount>` — fired by the bettor each spin; every client adds to its mirror.
  - `slot_pool win` — owner = winner. Each client reads its (identical, because ordered) pool value,
    resets it to 0, refreshes the pool meter; the winner's own client also does
    `GainGold(amount)` + `SyncLocalObtainedGold(amount)` (guarded by `LocalContext.IsMe`).
  - Single-player / fake-MP: mutate the pool locally without the wire (still a fun solo progressive
    pot).
- **UI:** a pool meter shown in the popup (and optionally the resting cabinet) so both players watch it
  climb. Paytable gains a "SHARED POOL (5%)" row.

---

## Net wires added (all via the built-in `ConsoleCmdGameAction` — no new `INetAction`)

| verb | payload | owner | handler (every client) |
|---|---|---|---|
| `slot_shop` | relic ids | local player | cache sender's shop relic list |
| `slot_take` | relicEntry | taker | peers clear that relic from their own shop |
| `slot_pool` | `add <n>` / `win` | bettor / winner | add to / reset shared pool mirror |

Plus the built-in `RewardSynchronizer.SyncLocal*` for gold/relic mutations (not a new wire).

**Lockstep note:** both clients must run the same mod version (the net type-id table sorts built-in +
mod net types by short name; a peer without the mod, or a different mod set, diverges). Normal co-op
mod constraint. If the `slot_shop`/`slot_pool` argument format ever changes, both sides must update
together.

---

## Desync guards (coop-guard checklist)

- **Class 1 (ordering):** no combat hooks touched; all dispatch is out-of-combat shop UI. The pool
  add/win and deplete ride the ordered action queue, so they replay in a consistent order. Not in the
  entry draw loop. ✔
- **Class 2 (non-determinism):** each spin's roll is `System.Random`, but a spin is a **per-player**
  action whose result affects only that player — no cross-client re-derivation is required (unlike
  RelicForge). The only *synced* values (pool total, peer shop list) are integers/ids applied in queue
  order, not re-rolled. ✔
- **Class 3 (source):** every state mutation replicates — gold/relic via `SyncLocal*`, pool via the
  queue, shop-deplete via `slot_take`. No per-client ModConfig feeds shared sim (the skip/manual
  toggles are purely local UI). ✔
- **Class 4 (echo):** grants use the same explicit sync-once pattern as the vanilla purchase; the pool
  win grants gold once (`LocalContext.IsMe` guard). ✔
- **Combat guard:** the `slot` test command can open the machine anywhere → gate all `SyncLocal*` and
  reward grants on `!CombatManager.Instance.IsInProgress`.

---

## Verification plan (final gate — needs a real 2-client session)

`multiplayer test` in-game → run two instances (Host + Join `127.0.0.1:33771`), isolate to just this
mod. Check:
1. Both players' own gold/relics update on their own spins; the peer's mirror stays consistent
   (**especially the free-shop-relic gold gotcha, §fact-3**).
2. Union pool: player A wins a relic from B's shop → A gets it, B's shop slot clears + toast.
3. Party-wide jackpot: A wins SignetRing → it disappears from B's reels.
4. Shared pool: both feed it, meter matches on both screens, a pool win pays the winner and resets to
   0 on both.
5. Enter combat afterwards → no desync / "match not entered" failure.
