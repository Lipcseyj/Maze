using KaoszRubin.Data;
using KaoszRubin.Domain.Characters;
using KaoszRubin.Domain.Inventory;

namespace KaoszRubin;

internal sealed class DoorInteractionController
{
    private static readonly Direction[] Directions = Enum.GetValues<Direction>();
    private readonly GameDataCatalog _gameData;
    private readonly ConsoleRenderer _renderer;
    private readonly Action<SoundEffect, LiveCharacter> _playActorSound;
    private readonly Random _random;

    public DoorInteractionController(GameDataCatalog gameData, ConsoleRenderer renderer,
        Action<SoundEffect, LiveCharacter> playActorSound, Random random)
    {
        _gameData = gameData;
        _renderer = renderer;
        _playActorSound = playActorSound;
        _random = random;
    }

    public void TryOpenAdjacentDoor(Maze maze, FogOfWar fogOfWar, Position actorPosition, Position leaderPosition,
        LiveCharacter selectedCharacter, bool allowPartyAssistanceAndPrompts, Position? targetDoorPosition = null,
        bool? useKeyChoice = null, CharacterId? keyOwnerCharacterId = null,
        IReadOnlyList<LiveCharacter>? availableKeyOwners = null)
    {
        var door = GetAdjacentDoor(maze, actorPosition, targetDoorPosition);
        if (door is null) { _renderer.DrawDoorMessage("Nincs ajtó melletted."); return; }
        if (door.State == DoorState.Open) { _renderer.DrawDoorMessage("Az ajtó már nyitva van."); return; }
        if (door.State == DoorState.Smashed) { _renderer.DrawDoorMessage("A bezúzott ajtónyílás már szabad."); return; }
        if (door.State == DoorState.Closed)
        {
            maze.SetDoorState(door, DoorState.Open);
            RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                "Kinyitottad az ajtót.", ConsoleColor.Green);
            return;
        }

        var keyOwner = DoorInteractionRules.SelectKeyOwner(selectedCharacter,
            availableKeyOwners ?? [selectedCharacter], useKeyChoice, keyOwnerCharacterId);
        if (useKeyChoice == true && keyOwnerCharacterId is not null && keyOwner is null)
        {
            _renderer.DrawDoorMessage("A kiválasztott partitag hátizsákjában már nincs használható kulcs.",
                ConsoleColor.Red);
            return;
        }
        if (keyOwner is not null && keyOwner.RemoveFromBackpack(MiscItemIds.Key))
        {
            maze.SetDoorState(door, DoorState.Open);
            RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                keyOwner == selectedCharacter
                    ? "A kulcs kinyitotta a zárat és eltört a használat során."
                    : $"{keyOwner.Name} kulcsa kinyitotta a zárat és eltört a használat során.",
                ConsoleColor.Green);
            return;
        }

        var assistingThief = !allowPartyAssistanceAndPrompts || CharacterClassRules.IsThief(selectedCharacter.CharacterClass.Id)
            ? null
            : FindNearbyNpcThief(maze, actorPosition);
        var lockHandler = assistingThief?.Character ?? selectedCharacter;
        var isThief = CharacterClassRules.IsThief(lockHandler.CharacterClass.Id);

        var attemptCost = ConsumeLockedDoorAttemptNeeds(lockHandler);
        var costMessage = $" Próba ára: 🍖 -{attemptCost.Food}, 💧 -{attemptCost.Water}.";

        if (isThief)
        {
            var chance = LockpickChance(lockHandler.EffectiveAbilities.Dexterity);
            var roll = _random.Next(1, 101);
            if (roll <= chance)
            {
                maze.SetDoorState(door, DoorState.Open);
                RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                    $"{(assistingThief is null ? string.Empty : lockHandler.Name + " előrelép. ")}Zárnyitás sikerült: " +
                    $"Ügy {lockHandler.EffectiveAbilities.Dexterity}, esély {chance}%, dobás {roll}." + costMessage,
                    ConsoleColor.Green);
                return;
            }
            _renderer.DrawDoorMessage($"{(assistingThief is null ? string.Empty : lockHandler.Name + ": ")}Zárnyitás sikertelen: " +
                $"Ügy {lockHandler.EffectiveAbilities.Dexterity}, esély {chance}%, dobás {roll}." + costMessage,
                ConsoleColor.Red);
            if (assistingThief is not null && !_renderer.DrawDoorSmashChoice(selectedCharacter, lockHandler,
                    maze, fogOfWar, actorPosition))
            {
                _renderer.DrawDoorMessage($"{selectedCharacter.Name} nem próbálta betörni az ajtót. Az ajtó zárva marad.",
                    ConsoleColor.DarkYellow);
                return;
            }
        }

        var strengthRoll = _random.Next(1, 21);
        var racialStrengthBonus = selectedCharacter.Race.HasTrait(RaceTraits.Relentless) ? 2 : 0;
        var effectiveStrength = selectedCharacter.EffectiveAbilities.Strength + racialStrengthBonus;
        if (strengthRoll <= effectiveStrength)
        {
            maze.SetDoorState(door, DoorState.Smashed);
            RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                $"Erőpróba sikerült: 1d20({strengthRoll}) ≤ Erő {selectedCharacter.EffectiveAbilities.Strength}" +
                (racialStrengthBonus > 0 ? $" + faji bónusz {racialStrengthBonus}" : string.Empty) +
                ". Az ajtó bezúzva!" + costMessage,
                ConsoleColor.Green);
        }
        else
        {
            _renderer.RefreshCharacterSheet(selectedCharacter);
            _renderer.DrawDoorMessage(
                $"Erőpróba sikertelen: 1d20({strengthRoll}) > Erő {selectedCharacter.EffectiveAbilities.Strength}" +
                (racialStrengthBonus > 0 ? $" + faji bónusz {racialStrengthBonus}" : string.Empty) +
                ". Az ajtó zárva marad." + costMessage,
                ConsoleColor.Red);
        }
    }

    public void TryCloseAdjacentDoor(Maze maze, FogOfWar fogOfWar, Position actorPosition,
        Position leaderPosition, LiveCharacter selectedCharacter, Position? targetDoorPosition = null)
    {
        var door = GetAdjacentDoor(maze, actorPosition, targetDoorPosition);
        if (door is null) { _renderer.DrawDoorMessage("Nincs ajtó melletted."); return; }
        if (door.State == DoorState.Smashed) { _renderer.DrawDoorMessage("A bezúzott ajtó többé nem zárható be.", ConsoleColor.Red); return; }
        if (door.State == DoorState.Locked) { _renderer.DrawDoorMessage("Az ajtó már kulcsra van zárva."); return; }
        if (door.State == DoorState.Closed) { _renderer.DrawDoorMessage("Az ajtó már be van zárva."); return; }
        maze.SetDoorState(door, DoorState.Closed);
        RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
            "Bezártad az ajtót.", ConsoleColor.DarkYellow);
    }

    public void TryCloseOrLockAdjacentDoor(Maze maze, FogOfWar fogOfWar, Position actorPosition,
        Position leaderPosition, LiveCharacter selectedCharacter, bool allowPartyAssistanceAndPrompts,
        Position? targetDoorPosition = null,
        bool? useKeyChoice = null, CharacterId? keyOwnerCharacterId = null,
        IReadOnlyList<LiveCharacter>? availableKeyOwners = null)
    {
        var door = GetAdjacentDoor(maze, actorPosition, targetDoorPosition);
        if (door is null) { _renderer.DrawDoorMessage("Nincs ajtó melletted."); return; }
        if (door.State == DoorState.Open)
        {
            TryCloseAdjacentDoor(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                targetDoorPosition);
            return;
        }
        TryLockAdjacentDoor(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
            allowPartyAssistanceAndPrompts, targetDoorPosition, useKeyChoice, keyOwnerCharacterId,
            availableKeyOwners);
    }

    public void TryLockAdjacentDoor(Maze maze, FogOfWar fogOfWar, Position actorPosition,
        Position leaderPosition, LiveCharacter selectedCharacter, bool allowPartyAssistanceAndPrompts,
        Position? targetDoorPosition = null,
        bool? useKeyChoice = null, CharacterId? keyOwnerCharacterId = null,
        IReadOnlyList<LiveCharacter>? availableKeyOwners = null)
    {
        var door = GetAdjacentDoor(maze, actorPosition, targetDoorPosition);
        if (door is null) { _renderer.DrawDoorMessage("Nincs ajtó melletted."); return; }
        if (door.State == DoorState.Smashed) { _renderer.DrawDoorMessage("A bezúzott ajtó többé nem zárható kulcsra.", ConsoleColor.Red); return; }
        if (door.State == DoorState.Locked) { _renderer.DrawDoorMessage("Az ajtó már kulcsra van zárva."); return; }

        var keyOwners = availableKeyOwners ?? [selectedCharacter];
        var hasAvailableKey = keyOwners.Any(DoorInteractionRules.HasKey);
        var keyOwner = DoorInteractionRules.SelectKeyOwner(selectedCharacter,
            keyOwners, useKeyChoice, keyOwnerCharacterId);
        if (useKeyChoice == true && keyOwnerCharacterId is not null && keyOwner is null)
        {
            _renderer.DrawDoorMessage("A kiválasztott partitag hátizsákjában már nincs használható kulcs.",
                ConsoleColor.Red);
            return;
        }
        if (keyOwner is not null && keyOwner.RemoveFromBackpack(MiscItemIds.Key))
        {
            maze.SetDoorState(door, DoorState.Locked);
            RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                keyOwner == selectedCharacter
                    ? "Kulccsal bezártad az ajtót. A kulcs elveszett."
                    : $"{keyOwner.Name} kulcsával bezártad az ajtót. A kulcs elveszett.",
                ConsoleColor.DarkYellow);
            return;
        }
        var assistingThief = !allowPartyAssistanceAndPrompts ||
                             CharacterClassRules.IsThief(selectedCharacter.CharacterClass.Id)
            ? null
            : FindNearbyNpcThief(maze, actorPosition);
        var lockHandler = assistingThief?.Character ?? selectedCharacter;
        if (CharacterClassRules.IsThief(lockHandler.CharacterClass.Id))
        {
            if (hasAvailableKey && useKeyChoice == false)
            {
                var attemptCost = ConsumeLockedDoorAttemptNeeds(lockHandler);
                var chance = LockpickChance(lockHandler.EffectiveAbilities.Dexterity);
                var roll = _random.Next(1, 101);
                var costMessage = $" Próba ára: 🍖 -{attemptCost.Food}, 💧 -{attemptCost.Water}.";
                if (roll > chance)
                {
                    _renderer.RefreshCharacterSheet(selectedCharacter);
                    _renderer.DrawDoorMessage(
                        $"{(assistingThief is null ? string.Empty : lockHandler.Name + " előrelép. ")}" +
                        $"Zárás tolvajpróbája sikertelen: Ügy {lockHandler.EffectiveAbilities.Dexterity}, " +
                        $"esély {chance}%, dobás {roll}. Az ajtó zárva, de nem kulcsra zárva marad.{costMessage}",
                        ConsoleColor.Red);
                    return;
                }
                maze.SetDoorState(door, DoorState.Locked);
                RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                    $"{(assistingThief is null ? string.Empty : lockHandler.Name + " előrelép. ")}" +
                    $"Zárás tolvajpróbája sikerült: Ügy {lockHandler.EffectiveAbilities.Dexterity}, " +
                    $"esély {chance}%, dobás {roll}.{costMessage}", ConsoleColor.DarkYellow);
                return;
            }
            maze.SetDoorState(door, DoorState.Locked);
            RefreshAfterDoorChanged(maze, fogOfWar, actorPosition, leaderPosition, selectedCharacter,
                assistingThief is null
                    ? "Tolvajként kulcs nélkül is bezártad az ajtó zárját."
                    : $"{lockHandler.Name} előrelép, és tolvajként kulcs nélkül bezárja az ajtó zárját.",
                ConsoleColor.DarkYellow);
            return;
        }
        _renderer.DrawDoorMessage("Az ajtó kulcsra zárásához kulcs vagy tolvaj szükséges.", ConsoleColor.Red);
    }

    private static MazeDoor? GetAdjacentDoor(Maze maze, Position playerPosition, Position? targetDoorPosition)
    {
        if (targetDoorPosition is { } target)
            return Math.Abs(target.X - playerPosition.X) + Math.Abs(target.Y - playerPosition.Y) == 1
                ? maze.GetDoorAt(target)
                : null;
        var adjacentDoors = Directions.Select(direction => maze.GetDoorAt(playerPosition + direction))
            .Where(door => door is not null).ToArray();
        return adjacentDoors.Length == 1 ? adjacentDoors[0] : null;
    }

    private static PartyMemberAvatar? FindNearbyNpcThief(Maze maze, Position leaderPosition) =>
        maze.PartyMembers.Where(member => member.Character.IsAlive &&
                CharacterClassRules.IsThief(member.Character.CharacterClass.Id) &&
                Chebyshev(member.Position, leaderPosition) <= 2)
            .OrderByDescending(member => member.Character.EffectiveAbilities.Dexterity)
            .FirstOrDefault();

    private static int Chebyshev(Position first, Position second) =>
        Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));

    private (int Food, int Water) ConsumeLockedDoorAttemptNeeds(LiveCharacter selectedCharacter)
    {
        var rules = _gameData.DoorAttemptRules;
        var food = _random.Next(rules.FoodMinimum, rules.FoodMaximum + 1);
        var water = _random.Next(rules.WaterMinimum, rules.WaterMaximum + 1);
        selectedCharacter.ConsumeFood(food);
        selectedCharacter.ConsumeWater(water);
        selectedCharacter.SynchronizeNeedStatuses(
            _gameData.GetStatus(CharacterStatusIds.Hungry),
            _gameData.GetStatus(CharacterStatusIds.Thirsty));
        return (food, water);
    }

    private void RefreshAfterDoorChanged(Maze maze, FogOfWar fogOfWar, Position actorPosition,
        Position leaderPosition, LiveCharacter selectedCharacter, string message, ConsoleColor color)
    {
        fogOfWar.RevealFrom(maze, actorPosition);
        _renderer.DrawMapVisibilityChanged(maze, fogOfWar, leaderPosition);
        _renderer.RefreshCharacterSheet(selectedCharacter);
        _renderer.DrawDoorMessage(message, color);
        _playActorSound(message.Contains("Bezártad", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("bezártad", StringComparison.OrdinalIgnoreCase)
            ? SoundEffect.DoorClose
            : SoundEffect.DoorOpen, selectedCharacter);
    }

    private static int LockpickChance(int dexterity) => dexterity <= 10
        ? Math.Clamp(dexterity * 10 - 10, 0, 90)
        : Math.Clamp(90 + (dexterity - 10) * 10 / 3, 90, 100);
}
