using KaoszRubin.Data;
using KaoszRubin.Application;
using KaoszRubin.Domain.Characters;
using KaoszRubin.Combat;
using KaoszRubin.Domain.Inventory;
using KaoszRubin.Domain.Combat;
using KaoszRubin.Domain.Magic;
using KaoszRubin.Domain;
using KaoszRubin.UI;
using static KaoszRubin.GameInput;
using MainMenu = KaoszRubin.UI.MainMenu;

namespace KaoszRubin;

/// <summary>A játék futását és felhasználói bemenetét koordinálja.</summary>
public sealed class Game : ISessionCommandHandler
{
    #region Fields and Properties
    private const string EliraStoryId = "ELIRA_RESCUE";
    private const string RodericStoryId = "RODERIC_OATH";
    private const string RodericGraveRespectQuestId = "NPCQ040";
    private const string RodericInsigniaQuestId = "NPCQ037";
    private const string RodericSharedBattleQuestId = "NPCQ038";
    private const string RodericMalrecQuestId = "NPCQ039";
    private const int RodericPermanentJoinFriendliness = 8;
    private const int ZombieSpeed = 2;
    private const int ZombieMoveIntervalMilliseconds = 700;
    private const int MinimumPartyMoveDelayMilliseconds = 250;
    private const int MaximumPartyMoveDelayMilliseconds = 300;
    private const int CatchUpMoveDelayMilliseconds = 90;
    private const int ControlledMoveDelayMilliseconds = 85;
    private static readonly Direction[] Directions = Enum.GetValues<Direction>();
    private const int MazeWidth = ConsoleRenderer.PlayfieldWidth;
    private const int MazeHeight = ConsoleRenderer.PlayfieldHeight;
    private readonly GameDataCatalog _gameData;
    private MazeGenerator _generator = null!;
    private readonly ConsoleRenderer _renderer;
    private Maze _maze = null!;
    private Player _player = null!;
    private FogOfWar _fogOfWar = null!;
    private readonly Random _random = new();
    private readonly BattleSystem _battleSystem;
    private BattleActionDetails? _lastBattleActionDetails;
    private readonly GameSaveService _gameSaveService;
    private readonly GameStateMapper _gameStateMapper;
    private readonly DoorInteractionController _doorInteractions;
    private readonly InnController _innController;
    private ICoopHostLoop? _activeCoopHost;
    private NarrativeSnapshot? _activeNarrative;
    private AdHocConversationSnapshot? _activeAdHocConversation;
    private readonly HashSet<string> _usedAdHocConversationIds = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastAdHocConversationUtc = DateTime.MinValue;
    private DateTime _nextAdHocConversationCheckUtc = DateTime.MinValue;
    private int _adHocConversationMazeLevel = -1;
    private readonly HashSet<PlayerId> _narrativeAcknowledgements = [];
    private LevelImageSnapshot? _activeLevelImage;
    private readonly HashSet<PlayerId> _levelImageAcknowledgements = [];
    private InnDepartureSnapshot? _activeInnDeparture;
    private SpellPreparationSnapshot? _activeSpellPreparation;
    private bool _spellPreparationCompleted;
    private PartyRestSnapshot? _latestRestNotice;
    private readonly HashSet<PlayerId> _restAcknowledgements = [];
    private readonly List<string> _hostRestAcknowledgementMessages = [];
    private LevelUpPromptSnapshot? _activeLevelUpPrompt;
    private string? _levelUpResponse;
    private bool _levelUpPromptCompleted;
    private readonly GameSaveData? _loadedState;
    private readonly SoundEffects _soundEffects;
    private readonly BackgroundMusicPlayer _backgroundMusic;
    private readonly GameSettingsService _musicSettings;
    private readonly GameSession _session;
    private readonly SpellExecutionService _spellExecutionService;
    private readonly SingleBattleCoordinator _singleBattleCoordinator;
    private readonly TacticalTeamBattleCoordinator _teamBattleCoordinator;
    private readonly NpcQuestCoordinator _npcQuestCoordinator;
    private readonly StoryConversationCoordinator _storyConversationCoordinator;
    private readonly CharacterProgressionService _progressionService;
    private readonly PartySustenanceService _sustenanceService;
    private readonly DungeonTrapService _dungeonTrapService;
    private readonly LootAndInventoryService _lootService;
    private readonly DungeonExpeditionCoordinator _expeditionCoordinator;
    private readonly SessionEventService _sessionEventService;
    private readonly PartyCommandController _partyCommandController;
    private readonly PartyAiController _partyAiController;
    private readonly SessionCommandDispatcher _commandDispatcher;
    private long _localCommandId;
    private TeamBattleEncounter? _activeTeamBattle;
    private bool _isQuickTeamBattle;
    private int _quickBattleSuppressedEntryCount;
    private long _preparedTeamBattleTurnId;
    private long _teamMovementTurnId = -1;
    private int _teamMovementRemaining;
    private int _teamMovementSteps;
    private bool _battleStarted;
    private bool _gameOver;
    private bool _characterSheetFocused;
    private HeldInventoryItem? _heldInventoryItem;
    private DateTime _nextNeedsDrain;
    private DateTime _nextNpcSelfCareCheck;
    private readonly Dictionary<Enemy, DateTime> _nextEnemyMoves = [];
    private readonly Dictionary<PartyMemberAvatar, DateTime> _nextPartyMoves = [];
    private readonly Dictionary<CharacterId, DateTime> _nextControlledMoves = [];
    private readonly List<Position> _leaderTrail = [];
    private bool _partyHoldingPosition;
    private bool _partyRegrouping;
    private bool _partyAttackMode;
    private PartyCommandState _partyCommandState;
    private bool _saveAfterBattle;
    private bool _timeStopUsedThisBattle;
    private readonly HashSet<LiveCharacter> _turnUndeadUsedThisBattle = [];
    private readonly HashSet<CharacterId> _battleNoPathReported = [];
    private int _battleLogCycle = -1;
    private readonly Dictionary<(CharacterId CharacterId, NpcComplaintKind Kind), DateTime> _nextNpcComplaints = [];
    private readonly HashSet<(CharacterId CharacterId, NpcComplaintKind Kind)> _reportedNpcShortages = [];
    private readonly List<(LiveCharacter Character, LevelUpResult Result)> _pendingLevelUps = [];
    private readonly Dictionary<string, QuestJournalEntrySnapshot> _questJournal =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PlayerId> _helpPausePlayers = [];
    private DateTime? _helpPauseStartedUtc;
    private DateTime? _partyScatterUntil;
    private Direction _leaderFacing = Direction.Right;
    private PartyFormationSnapshot _formation;
    private bool _formationObstacleReported;
    private int _mazeLevel = 1;
    private AdventureLocationKind _locationKind = AdventureLocationKind.Campaign;
    private string _locationId = string.Empty;
    private int _difficultyLevel = 1;
    private GameSaveData? _suspendedCampaignState;
    private bool _pendingRodericExpedition;
    private bool _pendingRodericReturn;
    private bool _hasRestedThisLevel;
    private bool _developerPhasing;
    private int _lastDeveloperUniqueNpcIndex = -1;
    private readonly HashSet<string> _collectedBossKeyIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenBossIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<WorldEntityId> _spottedEnemyIds = [];
    private readonly HashSet<WorldEntityId> _spottedChestIds = [];
    private readonly List<ExpeditionEnemyTemplate> _levelEnemyTemplates = [];
    private readonly List<WorldNpc> _temporaryFollowersEnteringNextMaze = [];
    private bool _isReturnExpedition;
#endregion

    public CharacterRoster CharacterRoster { get; }
    public LiveCharacter SelectedCharacter { get; }
    public GameSession Session => _session;
    public TeamBattleEncounter? ActiveTeamBattle => _activeTeamBattle;

    public SessionSnapshot CreateSessionSnapshot()
    {
        if (_maze is null || _player is null)
            throw new InvalidOperationException("Session snapshot csak inicializált játékból készíthető.");
        var positions = new Dictionary<CharacterId, Position>
        {
            [SelectedCharacter.Id] = _player.Position
        };
        foreach (var member in _maze.PartyMembers) positions[member.Character.Id] = member.Position;

        BattleSnapshot? battle = _activeTeamBattle is { IsCompleted: false } teamBattle
            ? CreateTeamBattleSnapshot(teamBattle)
            : null;
        var snapshot = _session.CreateSnapshot(new SessionSnapshotContext(_difficultyLevel, _maze.LevelName,
            positions, battle, WorldSnapshotProjector.Create(_maze, _fogOfWar, null,
                _activeTeamBattle?.Enemies.Where(enemy => enemy.CurrentHitPoints > 0)
                    .Select(enemy => enemy.Id).ToHashSet())));
        var followers = _maze.PartyMembers
            .Where(member => member.IsTemporaryFollower)
            .Select(member => member.Character)
            .Distinct()
            .ToArray();
        var characters = CharacterRoster.Party.Members.Concat(followers).ToDictionary(character => character.Id);
        var followerSnapshots = followers.Select(character => new SessionCharacterSnapshot(
            character.Id, character.Name, character.Race.Id, character.CharacterClass.Id, character.Level,
            character.CurrentVitality, character.MaximumVitality, character.CurrentMana, character.MaximumMana,
            character.FoodLevel, character.WaterLevel, SelectedCharacter.Gold, character.IsAlive,
            positions.GetValueOrDefault(character.Id), character.Statuses.Select(status => status.Id).ToArray(),
            Inventory: InventorySnapshotProjector.Create(character),
            CharacterSheet: CharacterSheetSnapshotProjector.Create(character,
                _gameData.ExperienceByLevel, CurrentLevelVisionModifier), Color: character.Color,
            IsTemporaryFollower: true, History: CreateCharacterHistory(character))).ToArray();
        return snapshot with
        {
            GoldenKeyCount = _collectedBossKeyIds.Count,
            BossKeyCount = MonsterIds.Bosses.Count,
            Inn = _innController.CreateSnapshot(),
            InnDeparture = _activeInnDeparture,
            Narrative = _activeNarrative is null ? null : _activeNarrative with
            { AcknowledgedPlayerIds = _narrativeAcknowledgements.ToArray() },
            LevelImage = _activeLevelImage is null ? null : _activeLevelImage with
            { AcknowledgedPlayerIds = _levelImageAcknowledgements.ToArray() },
            SpellPreparation = _activeSpellPreparation,
            RestNotice = _latestRestNotice is null ? null : _latestRestNotice with
            { AcknowledgedPlayerIds = _restAcknowledgements.ToArray() },
            LevelUpPrompt = _activeLevelUpPrompt,
            Activities = _sessionEventService.Activities,
            Sounds = _sessionEventService.Sounds,
            PartyGold = SelectedCharacter.Gold,
            QuestJournal = OrderedQuestJournal(),
            AdHocConversation = _activeAdHocConversation,
            Formation = _formation,
            Party = snapshot.Party.Select(character => character with
            {
                Gold = SelectedCharacter.Gold,
                CharacterSheet = CharacterSheetSnapshotProjector.Create(characters[character.CharacterId],
                    _gameData.ExperienceByLevel, CurrentLevelVisionModifier),
                History = CreateCharacterHistory(characters[character.CharacterId]),
                SpellInfo = SpellcastingRules.TryGetSchool(characters[character.CharacterId].CharacterClass.Id, out _)
                    ? SpellInfoSnapshotProjector.Create(characters[character.CharacterId]) : null,
                ExplorationSpellOptions = snapshot.Phase == GameSessionPhase.Exploration &&
                                          positions.TryGetValue(character.CharacterId, out var characterPosition)
                    ? GetSpellOptions(characters[character.CharacterId], characterPosition, null, inCombat: false)
                    : null
            }).Concat(followerSnapshots).ToArray()
        };
    }

    private BattleSnapshot CreateTeamBattleSnapshot(TeamBattleEncounter battle)
    {
        var current = battle.Current;
        var actingCharacter = battle.CurrentCharacter;
        var focusEnemy = battle.CurrentEnemy ?? battle.SelectedTargetEnemy() ?? (actingCharacter is null ? null :
            ReachableTeamEnemies(battle, actingCharacter).OrderBy(enemy => enemy.CurrentHitPoints).FirstOrDefault()) ??
            battle.Enemies.Where(enemy => enemy.CurrentHitPoints > 0)
                .OrderBy(enemy => TacticalDistance.Between(current.Position, enemy.Position)).First();
        var actingCharacterId = actingCharacter?.Id ?? SelectedCharacter.Id;
        var allowed = actingCharacter is null
            ? new[] { BattleActionKind.AdvanceEnemyTurn }
            : GetTeamAllowedBattleActions(battle, actingCharacter, focusEnemy);
        var spellOptions = actingCharacter is not null && allowed.Contains(BattleActionKind.CastSpell)
            ? GetSpellOptions(actingCharacter, GetCasterPosition(actingCharacter), focusEnemy, inCombat: true)
            : null;
        var focusTargetId = TeamBattleFocusTarget(battle, current);
        var participants = battle.Turns.Participants.Select(participant =>
        {
            var character = battle.CharacterFor(participant.Id);
            var enemy = battle.EnemyFor(participant.Id);
            return new TacticalBattleParticipantSnapshot(participant.Id,
                character?.Name ?? enemy?.Name ?? participant.Id.Value,
                participant.Side, participant.Kind, participant.Position, participant.InitiativeBase,
                participant.Id == current.Id && _teamMovementTurnId == battle.Turns.TurnId
                    ? _teamMovementRemaining : participant.MovementAllowance,
                participant.EligibleFromCycle, participant.State,
                character?.CurrentVitality ?? enemy?.CurrentHitPoints ?? 0,
                character?.MaximumVitality ?? enemy?.Definition.HitPoints ?? 0,
                participant.Id == current.Id, participant.Id == focusTargetId, enemy?.Id);
        }).OrderByDescending(participant => participant.Initiative).ToArray();
        return new BattleSnapshot(battle.Id, battle.Turns.TurnId, battle.ActionNumber,
            actingCharacter is not null, actingCharacterId,
            new SessionEnemySnapshot(focusEnemy.Definition.Id, focusEnemy.Name, focusEnemy.Position,
                focusEnemy.CurrentHitPoints, focusEnemy.Definition.HitPoints ?? focusEnemy.CurrentHitPoints,
                focusEnemy.Id),
            allowed, spellOptions,
            actingCharacter is null ? null : GetTeamBattleTacticOptions(battle, actingCharacter, focusEnemy),
            battle.Turns.Cycle, participants,
            actingCharacter is null ? null : GetBattleItemOptions(battle, actingCharacter),
            actingCharacter is null ? null : ReachableTeamEnemies(battle, actingCharacter)
                .Select(enemy => enemy.Id).ToArray(), IsQuickBattle: _isQuickTeamBattle,
            ActionDetails: _lastBattleActionDetails);
    }

    private IReadOnlyList<BattleItemOptionSnapshot> GetBattleItemOptions(TeamBattleEncounter battle,
        LiveCharacter character) => TacticalTeamBattleCoordinator.GetBattleItemOptions(battle, character);

    private static bool IsTeamBattleItemUseful(LiveCharacter character, MiscItemDefinition item) =>
        TacticalTeamBattleCoordinator.IsTeamBattleItemUseful(character, item);

    private static CharacterHistorySnapshot CreateCharacterHistory(LiveCharacter character) => new(
        character.MonsterKills.Select(pair => new MonsterKillSnapshot(pair.Key, pair.Value)).ToArray(),
        character.NpcJoinedMazeLevel, character.NpcJoinedLocation, character.NpcBehavior?.ToString());

    private SessionCharacterSnapshot CreateCharacterDetailsSnapshot(LiveCharacter character) => new(
        character.Id, character.Name, character.Race.Id, character.CharacterClass.Id, character.Level,
        character.CurrentVitality, character.MaximumVitality, character.CurrentMana, character.MaximumMana,
        character.FoodLevel, character.WaterLevel, SelectedCharacter.Gold, character.IsAlive, null,
        character.Statuses.Select(status => status.Id).ToArray(), InventorySnapshotProjector.Create(character),
        CharacterSheetSnapshotProjector.Create(character, _gameData.ExperienceByLevel, CurrentLevelVisionModifier),
        character.Color, SpellInfo: character.IsSpellcaster ? SpellInfoSnapshotProjector.Create(character) : null,
        History: CreateCharacterHistory(character));

    public Game(GameDataCatalog gameData, CharacterRoster characterRoster, LiveCharacter selectedCharacter,
        GameSaveService gameSaveService, GameSaveData? loadedState = null, GameSession? session = null,
        GameSettingsService? musicSettings = null)
    {
        CharacterRoster = characterRoster;
        SelectedCharacter = selectedCharacter;
        _gameData = gameData;
        _gameSaveService = gameSaveService;
        _formation = PartyFormationRules.CreateDefault(characterRoster.Party.Members.Select(member => member.Id),
            selectedCharacter.Id);
        _gameStateMapper = new GameStateMapper(gameData, characterRoster, selectedCharacter);
        _loadedState = loadedState;
        _session = session ?? new GameSession(characterRoster.Party, selectedCharacter);
        _renderer = new ConsoleRenderer(gameData, characterRoster.Party, () => _maze?.PartyMembers
            .Where(member => member.IsTemporaryFollower)
            .Select(member => member.Character)
            .ToArray() ?? []);
        _renderer.SetFormationStatus(_formation);
        _renderer.SetGoldenKeyCount(0);
        _musicSettings = musicSettings ?? new GameSettingsService();
        _soundEffects = new SoundEffects(_musicSettings.Settings,
            message => _renderer.DrawDeveloperMessage(message));
        _backgroundMusic = new BackgroundMusicPlayer(_musicSettings.Settings,
            message => _renderer.DrawDeveloperMessage(message));
        _doorInteractions = new DoorInteractionController(gameData, _renderer,
            (effect, actor) => PlaySessionSound(effect, [actor.Id]), _random);
        _innController = new InnController(gameData, characterRoster, selectedCharacter, _renderer,
            effect => PlaySessionSound(effect),
            _random, AwardExperienceResult, ResolvePerkOffers, PreparePartySpells, ReadInnKey,
            ShowSynchronizedRest, () => _maze?.PartyMembers
                .Where(member => member.IsTemporaryFollower)
                .Select(member => member.Character)
                .ToArray() ?? []);
        _battleSystem = new BattleSystem(_random, gameData.MonsterAbilities, gameData.Statuses,
            gameData.StrengthHitBonuses);
        _spellExecutionService = new SpellExecutionService(gameData, _random);
        _singleBattleCoordinator = new SingleBattleCoordinator(gameData, _battleSystem, _spellExecutionService, _random);
        _teamBattleCoordinator = new TacticalTeamBattleCoordinator(gameData, _battleSystem, _random);
        _npcQuestCoordinator = new NpcQuestCoordinator(gameData);
        _storyConversationCoordinator = new StoryConversationCoordinator(gameData, _random);
        _progressionService = new CharacterProgressionService(gameData, _random);
        _sustenanceService = new PartySustenanceService(gameData, _random);
        _dungeonTrapService = new DungeonTrapService(gameData, _random);
        _lootService = new LootAndInventoryService(gameData, _random);
        _expeditionCoordinator = new DungeonExpeditionCoordinator(gameData, _random);
        _sessionEventService = new SessionEventService(_renderer, _soundEffects, _random);
        _partyCommandController = new PartyCommandController(_random);
        _partyAiController = new PartyAiController(_random);
        _partyCommandState = new PartyCommandState(false, false, false, null);
        _commandDispatcher = new SessionCommandDispatcher(_session, this, selectedCharacter.Id);
    }

    // NPC spellcasting for combat
    private BattlePlayerAction? ChooseNpcBattlePlayerAction(PartyMemberAvatar member, Enemy enemy,
        LiveCharacter? supportedFighter = null, Action? onSpellCast = null)
    {
        var caster = member.Character;
        if (!caster.IsAlive) return null;
        if (CanTurnUndead(caster, enemy) && !_turnUndeadUsedThisBattle.Contains(caster))
            return ResolveTurnUndead(caster, enemy);
        if (!caster.IsSpellcaster || !caster.CanCastSpells) return null;
        // Emergency heal: any ally under 35% HP, within the spell's range of the caster
        var allies = CharacterRoster.Party.Members.Append(caster).Distinct().Where(c => c.IsAlive).ToList();
        var lowest = allies.OrderBy(c => (double)c.CurrentVitality / c.MaximumVitality).FirstOrDefault();
        if (lowest is not null && NpcSpellcastingPolicy.NeedsHealing(lowest))
        {
            foreach (var spell in caster.MemorizedSpells.Where(s => s.CanUseInCombat))
            {
                var effects = _gameData.GetSpellEffects(spell.Id);
                if (!effects.Any(e => e.Type == SpellEffectType.Heal)) continue;
                var manaCost = SpellcastingRules.EffectiveManaCost(caster, spell);
                if (caster.CurrentMana < manaCost) continue;
                var range = Math.Max(1, spell.Range);
                var reachable = allies.Where(c => Chebyshev(member.Position, GetCasterPosition(c)) <= range).ToList();
                if (reachable.Count == 0) continue;
                var target = reachable.OrderBy(c => (double)c.CurrentVitality / c.MaximumVitality).First();
                var emergency = NpcSpellcastingPolicy.IsEmergency(target);
                if (!NpcSpellcastingPolicy.CanSpendMana(caster, manaCost, emergency)) continue;
                // Cast heal
                var divine = caster.RecordDivineSpellCast(spell);
                caster.SpendMana(manaCost);
                PlaySessionSound(SoundEffect.DefensiveSpell, [caster.Id, target.Id]);
                var notes = new List<string>();
                foreach (var effect in effects.Where(e => e.Type == SpellEffectType.Heal))
                    ApplyHealingForCaster(effect, spell, new[] { target }, divine, notes, caster);
                var summary = notes.Count == 0 ? "" : $" {string.Join("; ", notes)}";
                var message = $"{caster.Name} elsüti: {spell.Name} → {target.Name}. -{manaCost} manna.{summary}";
                _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
                RecordSessionActivity(SessionActivityKind.Support, message, ConsoleColor.Green);
                _renderer.RefreshBattleStatusRows();
                onSpellCast?.Invoke();
                return new BattlePlayerAction(message, BattleLogKind.PlayerAttack, 0, 0);
            }
        }

        // Cure status if helpful, within the spell's range of the caster
        foreach (var spell in caster.MemorizedSpells.Where(s => s.CanUseInCombat))
        {
            var effects = _gameData.GetSpellEffects(spell.Id);
            if (!effects.Any(e => e.Type == SpellEffectType.CureStatus)) continue;
            var manaCost = SpellcastingRules.EffectiveManaCost(caster, spell);
            if (caster.CurrentMana < manaCost) continue;
            var range = Math.Max(1, spell.Range);
            var candidates = CharacterRoster.Party.Members.Where(c => c.IsAlive &&
                effects.SelectMany(e => SpellExecutionService.ParseEffectParameters(e.Parameter)).Any(p => c.HasStatus(p)) &&
                Chebyshev(member.Position, GetCasterPosition(c)) <= range).ToList();
            if (!candidates.Any()) continue;
            var targetChar = candidates.First();
            var divine = caster.RecordDivineSpellCast(spell);
            caster.SpendMana(manaCost);
            PlaySessionSound(SoundEffect.DefensiveSpell, [caster.Id, targetChar.Id]);
            var notes = new List<string>();
            foreach (var effect in effects.Where(e => e.Type == SpellEffectType.CureStatus))
                ApplyStatusCureForCaster(effect, [targetChar], notes);
            var message = $"{caster.Name} elsüti: {spell.Name} → {targetChar.Name}. -{manaCost} manna. {string.Join("; ", notes)}";
            _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
            RecordSessionActivity(SessionActivityKind.Support, message, ConsoleColor.Green);
            _renderer.RefreshBattleStatusRows();
            onSpellCast?.Invoke();
            return new BattlePlayerAction(message, BattleLogKind.PlayerAttack, 0, 0);
        }

        // Más karakter harcát támadó varázslattal csak valódi vészhelyzetben támogatják.
        // Saját harcukban ez a korlátozás nem érvényes.
        if (supportedFighter is not null && !ShouldUseOffensiveSupportSpell(supportedFighter, enemy)) return null;

        // Offensive spell against the enemy the leader is fighting (single-target only, don't waste area/direction spells on one foe)
        foreach (var spell in caster.MemorizedSpells.Where(s => s.CanUseInCombat && s.TargetType == SpellTargetType.Enemy))
        {
            var effects = _gameData.GetSpellEffects(spell.Id);
            if (!effects.Any(e => e.Type == SpellEffectType.Damage)) continue;
            var manaCost = SpellcastingRules.EffectiveManaCost(caster, spell);
            if (!NpcSpellcastingPolicy.CanSpendMana(caster, manaCost)) continue;
            if (!IsValidSpellTarget(member.Position, spell, enemy.Position, enemy)) continue;
            var divine = caster.RecordDivineSpellCast(spell);
            caster.SpendMana(manaCost);
            var listeners = new List<CharacterId> { caster.Id };
            if (supportedFighter is not null) listeners.Add(supportedFighter.Id);
            PlaySessionSound(SoundEffect.OffensiveSpell, listeners);
            var execution = ExecuteSpell(caster, member.Position, spell, enemy.Position, inCombat: true, enemy, divine);
            var message = $"{caster.Name} elsüti: {spell.Name} → {enemy.Name}. -{manaCost} manna. {execution.Summary}";
            _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
            RecordSessionActivity(SessionActivityKind.Support, message, ConsoleColor.Green);
            _renderer.RefreshBattleStatusRows();
            onSpellCast?.Invoke();
            return new BattlePlayerAction(message, BattleLogKind.PlayerAttack, execution.DamageToCurrentEnemy, execution.ExtraPlayerActions);
        }

        return null;
    }

    private bool ShouldUseOffensiveSupportSpell(LiveCharacter fighter, Enemy enemy)
    {
        var enemyCombatAbilities = (enemy.Definition.Strength ?? 0) + (enemy.Definition.Speed ?? 0);
        var fighterCombatAbilities = fighter.EffectiveAbilities.Strength + fighter.EffectiveAbilities.Dexterity;
        return fighter.CurrentVitality * 2 <= fighter.MaximumVitality ||
               enemy.Definition.IsBoss || enemy.Definition.StrengthTier >= 5 ||
               enemyCombatAbilities > fighterCombatAbilities;
    }

    // A globálisan megállított csatában csak az NPC-k adnak automatikus támogatást; más emberi karakter nem.
    private int TryPartyMembersActInBattle(LiveCharacter fighter, Enemy enemy)
    {
        var totalDamage = 0;
        foreach (var member in _maze.PartyMembers.Where(member => member.Character != fighter &&
                     member.Character.IsAlive && !_session.IsHumanControlled(member.Character.Id)))
        {
            member.Character.AdvanceSpellEffects();
            totalDamage += ChooseNpcBattlePlayerAction(member, enemy, fighter)?.DamageToEnemy ?? 0;
        }
        return totalDamage;
    }

    #region Spells

    // NPC spellcasting for exploration - simple heals/cures/buffs
    private void TryNpcCastExplorationSpell(PartyMemberAvatar member)
    {
        var caster = member.Character;
        if (!caster.IsAlive || !caster.IsSpellcaster || !caster.CanCastSpells) return;
        var manaReservePercent = 20;
        var manaReserve = Math.Max(0, caster.MaximumMana * manaReservePercent / 100);
        var healThresholdPercent = 50; // more generous during exploration
        var allies = CharacterRoster.Party.Members.Append(caster).Distinct().Where(c => c.IsAlive).ToList();
        var lowest = allies.OrderBy(c => (double)c.CurrentVitality / c.MaximumVitality).FirstOrDefault();
        if (lowest is not null && (double)lowest.CurrentVitality / lowest.MaximumVitality * 100 <= healThresholdPercent)
        {
            foreach (var spell in caster.MemorizedSpells.Where(s => s.CanUseDuringExploration))
            {
                var effects = _gameData.GetSpellEffects(spell.Id);
                if (!effects.Any(e => e.Type == SpellEffectType.Heal)) continue;
                var manaCost = SpellcastingRules.EffectiveManaCost(caster, spell);
                if (caster.CurrentMana < manaCost) continue;
                if (caster.CurrentMana - manaCost < manaReserve) continue;
                var divine = caster.RecordDivineSpellCast(spell);
                caster.SpendMana(manaCost);
                PlaySessionSound(SoundEffect.DefensiveSpell, [caster.Id, lowest.Id]);
                var notes = new List<string>();
                foreach (var effect in effects.Where(e => e.Type == SpellEffectType.Heal))
                    ApplyHealingForCaster(effect, spell, new[] { lowest }, divine, notes, caster);
                var summary = notes.Count == 0 ? "" : $" {string.Join("; ", notes)}";
                var message = $"{caster.Name} elsüti: {spell.Name} → {lowest.Name}. -{manaCost} manna.{summary}";
                _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
                RecordSessionActivity(SessionActivityKind.Support, message, ConsoleColor.Green);
                _renderer.RefreshCharacterSheet(SelectedCharacter);
                return;
            }
        }
    }

    private void ApplyCharacterEffectForCaster(LiveCharacter character, SpellEffectDefinition effect, SpellDefinition spell,
        ActiveSpellEffectType type, bool divineJudgment, LiveCharacter caster) =>
        _spellExecutionService.ApplyCharacterEffect(caster, character, effect, spell, type, divineJudgment);

    private void ApplyCharacterEffectsForCaster(IEnumerable<LiveCharacter> characters, SpellEffectDefinition effect,
        SpellDefinition spell, ActiveSpellEffectType type, bool divineJudgment, LiveCharacter caster) =>
        _spellExecutionService.ApplyCharacterEffects(caster, characters, effect, spell, type, divineJudgment);

    private void ApplyHealingForCaster(SpellEffectDefinition effect, SpellDefinition spell,
        IEnumerable<LiveCharacter> characters, bool divineJudgment, ICollection<string> notes, LiveCharacter caster) =>
        _spellExecutionService.ApplyHealing(caster, effect, spell, characters, divineJudgment, notes);

    private void ApplyStatusCureForCaster(SpellEffectDefinition effect, IEnumerable<LiveCharacter> characters,
        ICollection<string> notes) =>
        _spellExecutionService.ApplyStatusCure(effect, characters, notes);

#endregion

    #region Initialization and Lifecycle
    public void Run(ICoopHostLoop? coopHost = null)
    {
        _activeCoopHost = coopHost;
        Console.CursorVisible = false;
        if (_loadedState is null)
        {
            StartNewMaze(showLevelImage: false);
#if !DEBUG
            ShowSynchronizedNarrative(NarrativeKind.CampaignIntroduction, "A KÁOSZRUBIN KRÓNIKÁJA",
                "I. fejezet — A tizenkét aranykulcs", StoryNarratives.CampaignIntroduction);
#endif
            ShowLevelImage();
        }
        else RestoreGame(_loadedState);
        if (_loadedState is null) _nextNeedsDrain = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        _nextAdHocConversationCheckUtc = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        if (coopHost is not null)
            _renderer.DrawDeveloperMessage($"Coop host aktív: {coopHost.ConnectionHint}");
        try
        {
            while (!_gameOver)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);
                    if (_activeTeamBattle is not null && !_isQuickTeamBattle &&
                        GameInputBindings.BattleDetailsPageDirection(keyInfo) is var detailDirection && detailDirection != 0)
                    {
                        _renderer.PageBattleDetails(detailDirection);
                        continue;
                    }
                    if (keyInfo.Key is ConsoleKey.PageUp or ConsoleKey.PageDown)
                    {
                        _renderer.ScrollMessageLog(keyInfo.Key == ConsoleKey.PageUp);
                        continue;
                    }
                    if (GameInput.IsSettingsShortcut(keyInfo))
                    {
                        SettingsScreen.Show(_musicSettings, ApplyAudioSettings);
                        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
                        _renderer.SetCharacterSheetFocused(_characterSheetFocused);
                        continue;
                    }
                    if (_activeTeamBattle is not null)
                    {
                        HandleLocalBattleInput(keyInfo);
                        continue;
                    }
                    if (IsHelpShortcut(keyInfo))
                    {
                        ShowInGameHelp();
                        continue;
                    }
                    if (IsSaveGameShortcut(keyInfo))
                    {
                        SaveGame();
                        continue;
                    }
                    if (keyInfo.Key == ConsoleKey.Q)
                    {
                        ShowQuestJournal();
                        continue;
                    }
                    if (_renderer.IsSpellInfoPageOpen)
                    {
                        if (keyInfo.Key == ConsoleKey.Escape) _renderer.CloseSpellInfoPage();
                        else if (keyInfo.Key == ConsoleKey.UpArrow) _renderer.MoveSpellInfoSelection(-1);
                        else if (keyInfo.Key == ConsoleKey.DownArrow) _renderer.MoveSpellInfoSelection(1);
                        else if (TryGetQuickSpellIndex(keyInfo, out var spellSlot)) AssignSelectedSpellQuickSlot(spellSlot);
                        else if (keyInfo.Key == ConsoleKey.Enter) CastSelectedSpellInfo();
                        continue;
                    }
                    if (keyInfo.Key == ConsoleKey.V)
                    {
                        BeginExplorationSpellCasting();
                        continue;
                    }
                    if (TryGetQuickSpellIndex(keyInfo, out var quickSpellSlot))
                    {
                        var quickSpell = SelectedCharacter.QuickSpells[quickSpellSlot];
                        if (quickSpell is null)
                            _renderer.DrawInventoryMessage("Ez a varázslat-gyorshely üres.", ConsoleColor.DarkYellow);
                        else
                            BeginExplorationSpellCasting(quickSpell);
                        continue;
                    }
                    if (GameInputBindings.IsCharacterSheetToggle(keyInfo.Key))
                    {
                        if (_characterSheetFocused) CancelHeldInventoryItem();
                        _characterSheetFocused = !_characterSheetFocused;
                        _renderer.SetCharacterSheetFocused(_characterSheetFocused);
                        continue;
                    }
                    if (_characterSheetFocused)
                    {
                        if (keyInfo.Key == ConsoleKey.Escape)
                        {
                            if (ConfirmReturnToMainMenu()) { CancelHeldInventoryItem(); return; }
                            continue;
                        }
                        if (keyInfo.Key == ConsoleKey.A && _renderer.DisplayedCharacter == SelectedCharacter)
                        {
                            EditFormation();
                            continue;
                        }
                        switch (GameInputBindings.InventoryAction(keyInfo.Key))
                        {
                            case InventoryInputAction.MoveUp: _renderer.MoveCharacterSheetSelection(-1); break;
                            case InventoryInputAction.MoveDown: _renderer.MoveCharacterSheetSelection(1); break;
                            case InventoryInputAction.Drop: DropSelectedInventoryItem(); break;
                            case InventoryInputAction.Inspect: InspectSelectedInventoryItem(); break;
                            case InventoryInputAction.Use: UseSelectedInventoryItem(); break;
                            case InventoryInputAction.MoveItem: GrabOrPlaceInventoryItem(); break;
                            case InventoryInputAction.SplitStack: SplitSelectedInventoryStack(); break;
                            case InventoryInputAction.DistributeStack: DistributeSelectedInventoryStack(); break;
                            case InventoryInputAction.CharacterDetails: ShowCharacterDetails(); break;
                            case InventoryInputAction.GiveFollowerStack: GiveSelectedStackToFollower(); break;
                            default:
                                if (keyInfo.Key == ConsoleKey.LeftArrow) _renderer.MoveDisplayedPartyMember(-1);
                                else if (keyInfo.Key == ConsoleKey.RightArrow) _renderer.MoveDisplayedPartyMember(1);
                                else if (keyInfo.Key == ConsoleKey.Delete) DismissSelectedPartyMember();
                                break;
                        }
                        continue;
                    }
#if DEBUG
                    if (IsRevealMapShortcut(keyInfo))
                    {
                        var isMapRevealed = _fogOfWar.ToggleDeveloperReveal();
                        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
                        _renderer.DrawDeveloperMessage(isMapRevealed
                            ? "Fejlesztői mód: teljes térkép felfedve."
                            : "Fejlesztői mód: köd visszaállítva.");
                        continue;
                    }
                    if (IsNewMazeShortcut(keyInfo))
                    {
                        StartNewMaze();
                        continue;
                    }
                    if (IsTeleportToExitShortcut(keyInfo))
                    {
                        TeleportLeaderNearExit();
                        _player.Character.AddGold(1000);
                        continue;
                    }
                    if (IsTeleportToNextUniqueNpcShortcut(keyInfo))
                    {
                        TeleportLeaderToNextUniqueNpc();
                        continue;
                    }
                    if (IsLevelUpShortcut(keyInfo))
                    {
                        TriggerDeveloperLevelUp();
                        continue;
                    }
                    if (IsLevelUpPartyShortcut(keyInfo))
                    {
                        GrantPartyExperienceForDevelopment();
                        continue;
                    }
                    if (IsFillPartySetYShortcut(keyInfo))
                    {
                        FillPartyForDevelopment([CharacterClassIds.Harcos, CharacterClassIds.Mágus, CharacterClassIds.Lovag], "Y");
                        continue;
                    }
                    if (IsFillPartySetXShortcut(keyInfo))
                    {
                        FillPartyForDevelopment([CharacterClassIds.Barbár, CharacterClassIds.Tolvaj, CharacterClassIds.Pap], "X");
                        continue;
                    }
                    if (IsAddLevelOnePartyMemberShortcut(keyInfo))
                    {
                        AddLevelOnePartyMemberForDevelopment();
                        continue;
                    }
                    if (IsDeveloperPhasingShortcut(keyInfo))
                    {
                        ToggleDeveloperPhasing();
                        continue;
                    }
#endif

                    var key = keyInfo.Key;
                    if (key == ConsoleKey.Escape)
                    {
                        if (ConfirmReturnToMainMenu()) return;
                        continue;
                    }
                    SubmitLocalExplorationCommand(keyInfo);
                }

                ProcessSessionCommands();
                ContinueDisconnectedRemoteBattleAsNpc();

                if (!_battleStarted && ProcessPendingRodericTransition()) continue;

                if (_helpPausePlayers.Count > 0)
                {
                    if (coopHost?.ShouldPublish(DateTime.UtcNow) == true)
                        coopHost.TryPublish(CreateSessionSnapshot());
                    Thread.Sleep(20);
                    continue;
                }

                if (!_battleStarted) MoveEnemies();

                if (!_battleStarted) MovePartyMembers();

                if (!_battleStarted && DateTime.UtcNow >= _nextAdHocConversationCheckUtc)
                {
                    var now = DateTime.UtcNow;
                    _nextAdHocConversationCheckUtc = now + TimeSpan.FromMinutes(1);
                    TryStartAdHocFollowerConversation(now);
                }

                if (!_battleStarted && DateTime.UtcNow >= _nextNeedsDrain)
                {
                    DrainNeeds();
                    _nextNeedsDrain = DateTime.UtcNow + TimeSpan.FromMinutes(1);
                }

                if (!_battleStarted && DateTime.UtcNow >= _nextNpcSelfCareCheck)
                {
                    ProcessNpcSelfCare(DateTime.UtcNow);
                    _nextNpcSelfCareCheck = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                if (coopHost?.ShouldPublish(DateTime.UtcNow) == true)
                    coopHost.TryPublish(CreateSessionSnapshot());

                Thread.Sleep(20);
            }
        }
        finally
        {
            _backgroundMusic.Dispose();
            _soundEffects.Dispose();
            if (_activeCoopHost is not null)
            {
                PublishRemoteCharacterStates(CharacterSyncReason.SessionEnded);
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            }
            _activeCoopHost = null;
            Console.CursorVisible = true;
            try
            {
                Console.SetCursorPosition(0, Math.Min(ConsoleRenderer.ScreenRowCount - 1,
                    Math.Max(0, Console.BufferHeight - 1)));
            }
            catch (IOException)
            {
            }
        }
    }

    private void CompleteCampaign()
    {
        if (_collectedBossKeyIds.Count < MonsterIds.Bosses.Count)
        {
            _renderer.DrawInventoryMessage(
                $"A Káoszrubin körül még zárva kering néhány aranylakat. Kulcsok: {_collectedBossKeyIds.Count}/{MonsterIds.Bosses.Count}.",
                ConsoleColor.Yellow);
            return;
        }

        PlaySessionSound(SoundEffect.LevelComplete);
        PlaySessionSound(SoundEffect.Victory);
        ShowSynchronizedNarrative(NarrativeKind.CampaignFinale, "GRATULÁLUNK, KULCSHORDOZÓK!",
            "XV. fejezet — A csillagok választottai",
            StoryNarratives.CreateCampaignFinale(CharacterRoster.Party.Members.Where(character => character.IsAlive), SelectedCharacter.Name));
        _gameOver = true;
        _session.SetPhase(GameSessionPhase.GameOver);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private void StartNewMaze(bool showLevelImage = true)
    {
        _locationKind = AdventureLocationKind.Campaign;
        _locationId = $"CAMPAIGN_{_mazeLevel:00}";
        _difficultyLevel = _mazeLevel;
        _suspendedCampaignState = null;
        _session.SetPhase(GameSessionPhase.Exploration);
        _session.SynchronizeParty();
        NormalizeFormation();
        _formation = PartyFormationRules.WithState(_formation, PartyFormationState.Disbanded);
        _renderer.SetFormationStatus(_formation);
        _session.SetFormationMovementLocked(false);
        _hasRestedThisLevel = false;
        _spottedEnemyIds.Clear();
        _spottedChestIds.Clear();
        foreach (var character in CharacterRoster.Party.Members)
        {
            character.ResetLevelResurrection();
            character.ResetLevelRelentless();
        }
        var configuration = MazeLevelConfigurations.Get(_mazeLevel);
        ResolvedEnemyEncounter ResolveEncounter(EnemyEncounterConfiguration encounter) => new(
            encounter.GroupCount,
            encounter.Members.Select(member => new ResolvedEnemyGroupMember(
                _gameData.GetEnemy(member.EnemyId), member.Count, member.Role)).ToList(),
            encounter.MovementProfile);
        _generator = new MazeGenerator(configuration.CreateGenerationSettings(_random),
            configuration.RoomEncounters.Select(ResolveEncounter).ToList(),
            configuration.CorridorEncounters.Select(ResolveEncounter).ToList());
        _maze = _generator.Create(MazeWidth, MazeHeight);
        _player = new Player(_maze.Entrance, SelectedCharacter);
        _leaderTrail.Clear();
        _leaderTrail.Add(_player.Position);
        _nextPartyMoves.Clear();
        PlacePartyMembersNear(_player.Position);
        PlaceCarriedTemporaryFollowersNear(_player.Position);
        PlaceTraps(configuration);
        PlaceFirstSinglePlayerCompanion();
        PlaceConfiguredWorldNpcs();
        PlaceQuestRoomEnemies(configuration);
        CaptureExpeditionEnemyTemplates();
        _fogOfWar = new FogOfWar(_maze.Width, _maze.Height, CharacterClassRules.BaseVisionRange);
        RevealFor(SelectedCharacter, _player.Position);
        foreach (var member in _maze.PartyMembers) RevealFor(member.Character, member.Position);
        _battleStarted = false;
        _gameOver = false;
        InitializeEnemyMoveSchedule(DateTime.UtcNow);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
        if (configuration.VisionModifier < 0)
        {
            var darknessMessage = $"🌑 Extra sötét pálya: minden karakter látótávja {configuration.VisionModifier}.";
            _renderer.DrawInventoryMessage(darknessMessage, ConsoleColor.DarkRed);
            RecordSessionActivity(SessionActivityKind.System, darknessMessage, ConsoleColor.DarkRed);
        }
        CheckBossDiscovery(_maze.Enemies.Where(enemy => _fogOfWar.IsRevealed(enemy.Position)));
        PlaySessionSound(SoundEffect.LevelStart);
        _backgroundMusic.SynchronizeMazeLevel(_mazeLevel, _fogOfWar.IsRevealed(_maze.Exit));
        _activeInnDeparture = null;
        if (showLevelImage) ShowLevelImage();
        LogMazeAccessibilityCheck();
    }

    #endregion

        private void StartRodericQuestLocation()
    {
        var follower = FindRodericFollower() ??
            throw new InvalidOperationException("Roderic nélkül nem indítható el Sir Malrec küldetéshelyszíne.");
        _suspendedCampaignState = CreateGameSaveData();
        ActivateNpcQuest(follower, RodericMalrecQuestId);
        CarryPersistentTemporaryFollowers();

        _locationKind = AdventureLocationKind.Quest;
        _locationId = QuestLocationConfigurations.RodericMalrec;
        var configuration = QuestLocationConfigurations.Get(_locationId);
        _difficultyLevel = configuration.Level;
        _session.SetPhase(GameSessionPhase.Exploration);
        _session.SynchronizeParty();
        NormalizeFormation();
        _formation = PartyFormationRules.WithState(_formation, PartyFormationState.Disbanded);
        _renderer.SetFormationStatus(_formation);
        _session.SetFormationMovementLocked(false);
        _hasRestedThisLevel = false;
        _spottedEnemyIds.Clear();
        _spottedChestIds.Clear();

        ResolvedEnemyEncounter ResolveEncounter(EnemyEncounterConfiguration encounter) => new(
            encounter.GroupCount,
            encounter.Members.Select(member => new ResolvedEnemyGroupMember(
                _gameData.GetEnemy(member.EnemyId), member.Count, member.Role)).ToList(),
            encounter.MovementProfile);
        _generator = new MazeGenerator(configuration.CreateGenerationSettings(_random),
            configuration.RoomEncounters.Select(ResolveEncounter).ToList(),
            configuration.CorridorEncounters.Select(ResolveEncounter).ToList());
        _maze = _generator.Create(MazeWidth, MazeHeight);
        _player = new Player(_maze.Entrance, SelectedCharacter);
        _leaderTrail.Clear();
        _leaderTrail.Add(_player.Position);
        _nextPartyMoves.Clear();
        PlacePartyMembersNear(_player.Position);
        PlaceCarriedTemporaryFollowersNear(_player.Position);
        PlaceTraps(configuration);
        PlaceQuestRoomEnemies(configuration);
        CaptureExpeditionEnemyTemplates();
        _fogOfWar = new FogOfWar(_maze.Width, _maze.Height, CharacterClassRules.BaseVisionRange);
        RevealFor(SelectedCharacter, _player.Position);
        foreach (var member in _maze.PartyMembers) RevealFor(member.Character, member.Position);
        _battleStarted = false;
        InitializeEnemyMoveSchedule(DateTime.UtcNow);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
        _renderer.DrawInventoryMessage(
            "⚔ Küldetéshelyszín: Sir Malrec sírkápolnája (5. nehézség). A katakombák állapota megmaradt.",
            ConsoleColor.Cyan);
        _backgroundMusic.SynchronizeMazeLevel(_difficultyLevel, _fogOfWar.IsRevealed(_maze.Exit));
        LogMazeAccessibilityCheck();
    }

    private bool ProcessPendingRodericTransition()
    {
        if (_pendingRodericExpedition)
        {
            _pendingRodericExpedition = false;
            var roderic = FindRodericFollower() ??
                throw new InvalidOperationException("Roderic eltűnt a saját történeti átmenete előtt.");
            RunStoryConversation(roderic);
            if (!string.Equals(roderic.StoryStateId, "MALREC_APPROACH", StringComparison.OrdinalIgnoreCase))
                return true;
            StartRodericQuestLocation();
            return true;
        }
        if (!_pendingRodericReturn) return false;
        _pendingRodericReturn = false;
        if (FindRodericFollower() is { } returningRoderic) RunStoryConversation(returningRoderic);
        ShowSynchronizedNarrative(NarrativeKind.QuestTransition, "AZ ESKÜSZEGŐ BUKÁSA",
            "Visszatérés a katakombákba",
            [
                "Sir Malrec páncélja üresen roskad a kőre. Roderic sokáig hallgat, majd letérdel egykori bajtársa mellé.",
                "„A múltat nem változtathatom meg. De többé nem hagyom, hogy helyettem döntsön.” A lovag visszavezet benneteket ugyanahhoz a pillanathoz, amelyben elhagytátok a katakombákat."
            ]);
        RestoreSuspendedCampaign();
        if (ResolveFailedRodericOath()) return true;
        TryFinalizeRodericPermanentJoin();
        return true;
    }

    private WorldNpc? FindRodericFollower() => _maze.PartyMembers
        .Select(member => member.TemporaryFollower)
        .FirstOrDefault(follower => follower is not null &&
            string.Equals(follower.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase));

    private bool ResolveFailedRodericOath()
    {
        var avatar = _maze.PartyMembers.FirstOrDefault(member => member.TemporaryFollower is { } follower &&
            string.Equals(follower.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(follower.StoryStateId, "JOIN_REFUSED", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(follower.StoryStateId, "OATH_BROKEN", StringComparison.OrdinalIgnoreCase)));
        if (avatar?.TemporaryFollower is not { } roderic) return false;
        _maze.RemovePartyMember(avatar);
        _nextPartyMoves.Remove(avatar);
        CharacterRoster.Remove(roderic.Character);
        _session.SynchronizeParty();
        _renderer.DrawInventoryMessage(
            $"⚜ Roderic külön úton távozott. A viszonyotok {roderic.Friendliness}/10 volt; " +
            $"a végleges csatlakozáshoz legalább {RodericPermanentJoinFriendliness}/10 kellett volna.",
            ConsoleColor.DarkYellow);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        return true;
    }

    private int RodericTargetLevel()
    {
        var requested = SelectedCharacter.Level >= 7 ? SelectedCharacter.Level + 2 : 7;
        return Math.Min(requested, _gameData.ExperienceByLevel.Keys.DefaultIfEmpty(requested).Max());
    }

    private void RestoreSuspendedCampaign()
    {
        var suspended = _suspendedCampaignState ??
            throw new InvalidOperationException("A felfüggesztett katakombapálya nem található.");
        if (FindRodericFollower() is { } currentRoderic)
        {
            for (var index = 0; index < suspended.Maze.PartyAvatars.Count; index++)
            {
                var avatar = suspended.Maze.PartyAvatars[index];
                if (avatar.TemporaryFollower is not { } saved ||
                    !string.Equals(saved.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase)) continue;
                suspended.Maze.PartyAvatars[index] = avatar with
                {
                    TemporaryFollower = saved with
                    {
                        Disposition = currentRoderic.Disposition,
                        State = currentRoderic.State,
                        Friendliness = currentRoderic.Friendliness,
                        Behavior = currentRoderic.Behavior,
                        QuestIds = currentRoderic.QuestIds.ToList(),
                        Quests = currentRoderic.Quests.ToList(),
                        ConversationStage = currentRoderic.ConversationStage,
                        StoryStateId = currentRoderic.StoryStateId
                    }
                };
                break;
            }
        }

        var restored = _gameStateMapper.Restore(suspended);
        _mazeLevel = restored.MazeLevel;
        _locationKind = AdventureLocationKind.Campaign;
        _locationId = string.IsNullOrWhiteSpace(suspended.LocationId)
            ? $"CAMPAIGN_{_mazeLevel:00}" : suspended.LocationId;
        _difficultyLevel = suspended.DifficultyLevel > 0 ? suspended.DifficultyLevel : _mazeLevel;
        _suspendedCampaignState = null;
        _maze = restored.Maze;
        _player = restored.Player;
        _fogOfWar = restored.FogOfWar;
        _leaderFacing = restored.LeaderFacing;
        _formation = PartyFormationRules.Normalize(suspended.Formation,
            CharacterRoster.Party.Members.Select(member => member.Id), SelectedCharacter.Id);
        _renderer.SetFormationStatus(_formation);
        _session.SetFormationMovementLocked(_formation.State == PartyFormationState.Locked);
        _leaderTrail.Clear();
        _leaderTrail.AddRange(restored.LeaderTrail);
        _partyHoldingPosition = restored.PartyHoldingPosition;
        _partyRegrouping = restored.PartyRegrouping;
        _partyAttackMode = restored.PartyAttackMode;
        _hasRestedThisLevel = restored.HasRestedThisLevel;
        _partyScatterUntil = restored.PartyScatterUntil;
        _nextNeedsDrain = restored.NextNeedsDrain;
        _nextEnemyMoves.Clear();
        foreach (var enemyMove in restored.NextEnemyMoves) _nextEnemyMoves[enemyMove.Key] = enemyMove.Value;
        _nextPartyMoves.Clear();
        foreach (var member in _maze.PartyMembers) ScheduleNextPartyMove(member, DateTime.UtcNow);
        _spottedEnemyIds.Clear();
        _spottedChestIds.Clear();
        CaptureExpeditionEnemyTemplates();
        _battleStarted = false;
        _session.SetPhase(GameSessionPhase.Exploration);
        RevealFor(SelectedCharacter, _player.Position);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
        _renderer.DrawInventoryMessage("↩ Visszatértetek a katakombák ugyanazon pontjára.", ConsoleColor.Cyan);
        _backgroundMusic.SynchronizeMazeLevel(_difficultyLevel, _fogOfWar.IsRevealed(_maze.Exit));
    }

    private bool TryFinalizeRodericPermanentJoin()
    {
        var avatar = _maze.PartyMembers.FirstOrDefault(member => member.TemporaryFollower is { } follower &&
            string.Equals(follower.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase));
        if (avatar?.TemporaryFollower is not { } roderic ||
            !string.Equals(roderic.StoryStateId, "JOIN_ACCEPTED", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(roderic.StoryStateId, "JOIN_PENDING", StringComparison.OrdinalIgnoreCase)) return false;
        if (CharacterRoster.Party.Members.Count >= Party.MaximumSize)
        {
            roderic.SetStoryState("JOIN_PENDING");
            _renderer.DrawInventoryMessage(
                "⚜ Roderic letette az új esküt de a parti megtelt. Ideiglenes követő marad és az első felszabaduló helyre automatikusan belép.",
                ConsoleColor.DarkYellow);
            return false;
        }
        if (!CharacterRoster.Party.Add(roderic.Character)) return false;
        roderic.SetStoryState("JOINED");
        avatar.MakePermanent();
        _session.SynchronizeParty();
        _renderer.DrawInventoryMessage(
            "⚜ Roderic: „Amíg utunk közös a kardom a ti kardotok. A pajzsom a ti pajzsotok.” 🤝 Roderic végleg csatlakozott.",
            ConsoleColor.Green);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        return true;
    }

    private void ShowLevelImage()
    {
#if !DEBUG
        var fileName = ImageViewer.FileNameForLevel(_maze.LevelName);
        var path = Path.Combine(AppContext.BaseDirectory, "Kepek", fileName);

        if (_activeCoopHost is not null)
        {
            ShowSynchronizedLevelImage(fileName, path);
            return;
        }

        if (!ImageViewer.Show(path))
            _renderer.DrawDeveloperMessage($"Pályakép még nem található: {fileName}");
#endif

    }

    private void LogMazeAccessibilityCheck()
    {
        var report = _maze.CheckFullAccessibility();
        _renderer.DrawDeveloperMessage(report.IsFullyAccessible
            ? $"Bejárhatósági önellenőrzés: OK, mind a(z) {report.TotalWalkableCount} padló-/ajtócella elérhető."
            : $"Bejárhatósági önellenőrzés: HIBA, {report.UnreachablePositions.Count}/{report.TotalWalkableCount} cella nem érhető el " +
              $"(pl. {report.UnreachablePositions[0].X},{report.UnreachablePositions[0].Y}).");
    }

    private void PlaceTraps(MazeLevelConfiguration configuration)
    {
        var definitions = configuration.TrapIds.Select(_gameData.GetTrap)
            .Where(trap => trap.MinimumLevel <= _difficultyLevel).ToArray();
        if (definitions.Length == 0) return;
        var desiredCount = configuration.TrapCount.Roll(_random);
        var candidates = new List<Position>();
        for (var y = 0; y < _maze.Height; y++)
        for (var x = 0; x < _maze.Width; x++)
        {
            var position = new Position(x, y);
            if (!_maze.IsWalkable(position) || position == _maze.Entrance || position == _maze.Exit ||
                _maze.StartingRoom?.Contains(position) == true || _maze.GetObjectAt(position) is not null ||
                _maze.Rooms.Any(room => !room.AllowsRandomContent && room.Contains(position)) ||
                Manhattan(position, _maze.Entrance) < 6 ||
                _maze.Doors.Any(door => Manhattan(door.Position, position) <= 1)) continue;
            candidates.Add(position);
        }
        var placed = new List<Position>();
        foreach (var position in candidates.OrderBy(_ => _random.Next()))
        {
            if (placed.Any(existing => Manhattan(existing, position) < 3)) continue;
            _maze.AddTrap(new MazeTrap(position, definitions[_random.Next(definitions.Length)]));
            placed.Add(position);
            if (placed.Count >= desiredCount) break;
        }
    }

    private void PlaceFirstSinglePlayerCompanion()
    {
        if (_activeCoopHost is not null || _mazeLevel != 1 || CharacterRoster.Party.Members.Count != 1 ||
            _maze.WorldNpcs.Count != 0) return;

        var preferredClassIds = SelectedCharacter.CharacterClass.Id switch
        {
            CharacterClassIds.Harcos or CharacterClassIds.Barbár => new[] { CharacterClassIds.Pap, CharacterClassIds.Tolvaj },
            CharacterClassIds.Lovag => new[] { CharacterClassIds.Mágus, CharacterClassIds.Tolvaj },
            CharacterClassIds.Tolvaj => new[] { CharacterClassIds.Harcos, CharacterClassIds.Lovag },
            CharacterClassIds.Pap => new[] { CharacterClassIds.Harcos, CharacterClassIds.Barbár },
            CharacterClassIds.Mágus => new[] { CharacterClassIds.Lovag, CharacterClassIds.Harcos },
            _ => new[] { CharacterClassIds.Harcos }
        };
        var characterClass = _gameData.GetCharacterClass(preferredClassIds[_random.Next(preferredClassIds.Length)]);
        var recruit = new RandomCharacterGenerator(_gameData, _random).CreateLevelOne(characterClass,
            CharacterRoster.Characters.Select(character => character.Name).ToArray());

        var candidates = new List<Position>();
        for (var y = 0; y < _maze.Height; y++)
        for (var x = 0; x < _maze.Width; x++)
        {
            var position = new Position(x, y);
            var distance = Manhattan(position, _maze.Entrance);
            if (!_maze.IsWalkable(position) || position == _maze.Exit || _maze.GetObjectAt(position) is not null ||
                _maze.GetTrapAt(position) is not null || distance < 6 || distance > 14) continue;
            candidates.Add(position);
        }
        if (candidates.Count == 0) return;

        CharacterRoster.Add(recruit);
        var spawnPosition = candidates[_random.Next(candidates.Count)];
        _maze.AddWorldNpc(new WorldNpc(spawnPosition, "NPC-FIRST-COMPANION", recruit, NpcDisposition.Friendly,
            recruitable: true, isQuestNpc: false,
            "Elvesztem ebben az átkozott labirintusban. Együtt talán kijutunk — veletek tartok, fizetség nélkül.",
            friendliness: 10, behavior: NpcWorldBehavior.Friendly));
    }

    private void PlaceConfiguredWorldNpcs()
    {
        foreach (var encounter in _gameData.NpcEncounters.Where(value => value.MazeLevel == _mazeLevel))
        {
            var definition = _gameData.GetNpc(encounter.NpcId);
            if (definition.Unique && CharacterRoster.Characters.Any(character =>
                    string.Equals(character.Name, definition.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var candidates = new List<Position>();
            for (var y = 0; y < _maze.Height; y++)
            for (var x = 0; x < _maze.Width; x++)
            {
                var position = new Position(x, y);
                var distance = Manhattan(position, _maze.Entrance);
                var questRoom = encounter.QuestRoomId is { } roomId ? _maze.GetRoomByContentId(roomId) : null;
                var isEligibleRoom = questRoom is not null
                    ? questRoom.Contains(position)
                    : !_maze.Rooms.Any(room => !room.AllowsRandomContent && room.Contains(position));
                if (!_maze.IsWalkable(position) || position == _maze.Exit || _maze.GetObjectAt(position) is not null ||
                    _maze.GetTrapAt(position) is not null || _maze.GetDoorAt(position) is not null ||
                    !isEligibleRoom || questRoom is null &&
                    (distance < encounter.MinimumDistance || distance > encounter.MaximumDistance)) continue;
                candidates.Add(position);
            }
            if (candidates.Count == 0) continue;

            var generator = new RandomCharacterGenerator(_gameData, _random);
            var recruit = definition.Unique && _gameData.GetUniqueNpcCharacter(definition.Id) is not null
                ? new UniqueNpcCharacterFactory(_gameData).Create(definition,
                    string.Equals(definition.Id, "NPC021", StringComparison.OrdinalIgnoreCase)
                        ? RodericTargetLevel()
                        : null)
                : definition.Unique && definition.RaceId is { } raceId
                    ? generator.CreateUniqueRecruit(definition.Name, _gameData.GetRace(raceId),
                        _gameData.GetCharacterClass(definition.CharacterClassId), SelectedCharacter.Level)
                : generator.CreateRecruit(_gameData.GetCharacterClass(definition.CharacterClassId),
                    SelectedCharacter.Level, CharacterRoster.Characters.Select(character => character.Name).ToArray());
            CharacterRoster.Add(recruit);
            var friendliness = definition.Unique ? 4 : RollNpcFriendliness(definition);
            var dialogue = _gameData.GetNpcDialogues(definition.Id)
                .Where(value => friendliness >= value.MinimumFriendliness && friendliness <= value.MaximumFriendliness)
                .OrderBy(_ => _random.Next()).FirstOrDefault()?.Text ?? "Az idegen óvatosan végigmér benneteket.";
            var questIds = _gameData.GetNpcQuests(definition.Id).Select(quest => quest.Id).ToArray();
            _maze.AddWorldNpc(new WorldNpc(candidates[_random.Next(candidates.Count)], definition.Id, recruit,
                definition.Disposition, definition.Recruitable, questIds.Length > 0, dialogue,
                friendliness: friendliness, behavior: definition.Behavior, questIds: questIds,
                storyId: definition.StoryId));
        }
    }

    private void PlaceQuestRoomEnemies(MazeLevelConfiguration configuration)
    {
        foreach (var encounter in configuration.QuestRoomEnemyEncounters)
        {
            var room = _maze.GetRoomByContentId(encounter.RoomId) ??
                throw new InvalidOperationException($"A quest room nem található: '{encounter.RoomId}'.");
            var center = new Position(room.TopLeft.X + room.Width / 2, room.TopLeft.Y + room.Height / 2);
            var positions = room.InteriorPositions().Where(position => _maze.IsWalkable(position) &&
                    _maze.GetObjectAt(position) is null && _maze.GetTrapAt(position) is null &&
                    _maze.Doors.All(door => Manhattan(door.Position, position) > 1))
                .OrderBy(position => Manhattan(position, center)).Take(encounter.Count).ToArray();
            if (positions.Length < encounter.Count)
                throw new InvalidOperationException($"A(z) '{encounter.RoomId}' quest roomban nincs hely " +
                                                    $"{encounter.Count} ellenfélnek.");
            foreach (var position in positions)
            {
                var enemy = new ConfiguredEnemy(position, _gameData.GetEnemy(encounter.EnemyId), _random);
                enemy.ConfigureMovement(EnemyMovementProfile.Stationary, Direction.Right);
                enemy.ConfigureGroup($"QUEST:{encounter.RoomId}");
                if (encounter.GuaranteedItemId is { } itemId) enemy.ConfigureGuaranteedLoot([itemId]);
                _maze.AddEnemy(enemy);
            }
        }
    }

    private int RollNpcFriendliness(NpcDefinition definition)
    {
        var baseValue = definition.Disposition switch
        {
            NpcDisposition.Friendly => _random.Next(7, 10),
            NpcDisposition.Neutral => _random.Next(3, 8),
            _ => _random.Next(0, 4)
        };
        var modifier = definition.Behavior switch
        {
            NpcWorldBehavior.Friendly => 1,
            NpcWorldBehavior.Guarded => -1,
            NpcWorldBehavior.Aggressive => -2,
            _ => 0
        };
        return Math.Clamp(baseValue + modifier, 0, 10);
    }

    /// <summary>A rejtett csapda egyszer kap passzív észlelési próbát. A felfedezett aktív csapda
    /// megállítja a mozgást, amíg K-val hatástalanítják.</summary>
    private bool CanEnterTrap(LiveCharacter character, Position destination)
    {
        var trap = _maze.GetTrapAt(destination);
        if (trap is null || !trap.IsActive) return true;
        if (trap.State == TrapState.Detected)
        {
            ShowTrapMessage($"⚠️ {trap.Definition.Name} zárja el az utat. A mellette álló karakter K-val megpróbálhatja hatástalanítani.",
                ConsoleColor.Yellow, character);
            return false;
        }
        if (!trap.DetectionAttempted)
        {
            trap.MarkDetectionAttempted();
            var chance = TrapDetectionChance(character, trap.Definition);
            if (_random.Next(100) < chance)
            {
                trap.Detect();
                _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
                RewardTrapSuccess(character, trap.Definition.DetectionExperience,
                    $"👁️ {character.Name} időben felfedezte: {trap.Definition.Name} ({chance}% esély).",
                    ConsoleColor.Cyan);
                return false;
            }
        }
        return true;
    }

    private static int TrapDetectionChance(LiveCharacter character, TrapDefinition definition) =>
        DungeonTrapService.TrapDetectionChance(character, definition);

    private static int TrapDisarmChance(LiveCharacter character, TrapDefinition definition) =>
        DungeonTrapService.TrapDisarmChance(character, definition);

    private bool TryDisarmAdjacentTrap(LiveCharacter character, Position position)
    {
        var traps = Directions.Select(direction => _maze.GetTrapAt(position + direction))
            .Where(trap => trap is { State: TrapState.Detected }).Cast<MazeTrap>().ToArray();
        if (traps.Length == 0) return false;
        var trap = traps[0];
        var chance = TrapDisarmChance(character, trap.Definition);
        if (_random.Next(100) < chance)
        {
            trap.Disarm();
            RegisterNpcQuestProgress(NpcQuestType.Disarm, "ANY");
            _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
            RewardTrapSuccess(character, trap.Definition.DisarmExperience,
                $"🧰 {character.Name} hatástalanította: {trap.Definition.Name} ({chance}% esély).",
                ConsoleColor.Green);
            return true;
        }
        trap.RecordFailedDisarm();
        ShowTrapMessage($"⚠️ {character.Name} nem tudta hatástalanítani: {trap.Definition.Name} ({chance}% esély)." +
                        (trap.FailedDisarmAttempts == 1 ? " A csapda még nem sült el." : string.Empty),
            ConsoleColor.DarkYellow, character);
        if (trap.FailedDisarmAttempts >= 2 && _random.Next(2) == 0) ApplyTrap(character, trap);
        return true;
    }

    private void TriggerTrapAt(LiveCharacter character, Position position)
    {
        if (_maze.GetTrapAt(position) is { IsActive: true } trap) ApplyTrap(character, trap);
    }

    private void ApplyTrap(LiveCharacter character, MazeTrap trap) =>
        _dungeonTrapService.ApplyTrap(character, trap, _difficultyLevel, _maze,
            (c, radius) =>
            {
                foreach (var enemy in _maze.Enemies.Where(enemy => Manhattan(enemy.Position, trap.Position) <= radius))
                    enemy.ConfigureMovement(enemy.MovementProfile, enemy.PatrolDirection, EnemyPursuitState.Pursuing);
            },
            ShowTrapMessage, c => _renderer.RefreshCharacterSheet(c),
            () => _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position));

    private void ShowTrapMessage(string message, ConsoleColor color, LiveCharacter character)
    {
        _renderer.DrawInventoryMessage(message, color);
        RecordSessionActivity(SessionActivityKind.System, message, color, [character.Id]);
    }

    private void RewardTrapSuccess(LiveCharacter character, int experience, string message, ConsoleColor color)
    {
        var award = AwardExperience(character, experience);
        var levelText = award.Result.LeveledUp
            ? $" Szint: {award.Result.PreviousLevel}→{award.Result.CurrentLevel}."
            : string.Empty;
        ShowTrapMessage($"{message} +{award.Result.GainedExperience} XP.{levelText}", color, character);
        _renderer.RefreshCharacterSheet(character);
        if (!award.Result.LeveledUp || !character.IsAlive) return;
        ResolvePerkOffers(character, award.Result);
        _renderer.RefreshCharacterSheet(character);
    }

    #region UI & Input

    private void ShowInGameHelp()
    {
        var synchronizeCoopPause = _activeCoopHost is not null;
        if (synchronizeCoopPause)
        {
            SetHelpVisibility(_session.HostPlayerId, SelectedCharacter.Id, true);
            _activeCoopHost!.TryPublish(CreateSessionSnapshot());
        }
        try
        {
            MainMenu.ShowHelp();
        }
        finally
        {
            if (synchronizeCoopPause)
            {
                SetHelpVisibility(_session.HostPlayerId, SelectedCharacter.Id, false);
                _activeCoopHost!.TryPublish(CreateSessionSnapshot());
            }
        }
    }

    private void AssignSelectedSpellQuickSlot(int slotIndex)
    {
        var character = _renderer.SpellInfoCharacter;
        var spell = _renderer.GetSelectedSpellInfo();
        if (character is null || spell is null) return;
        if (!character.AssignQuickSpell(slotIndex, spell))
        {
            _renderer.DrawInventoryMessage("Csak memorizált varázslat tehető gyorshelyre.", ConsoleColor.Red);
            return;
        }
        _renderer.RefreshSpellInfoPage();
        _renderer.DrawInventoryMessage($"{spell.Name} hozzárendelve: F{slotIndex + 1}.", ConsoleColor.Cyan);
    }

    private void CastSelectedSpellInfo()
    {
        var character = _renderer.SpellInfoCharacter;
        var spell = _renderer.GetSelectedSpellInfo();
        if (character != SelectedCharacter || spell is null ||
            character.MemorizedSpells.All(candidate => !string.Equals(candidate.Id, spell.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _renderer.DrawInventoryMessage("Csak a partivezér memorizált varázslata süthető el.", ConsoleColor.DarkYellow);
            return;
        }
        _renderer.CloseSpellInfoPage();
        BeginExplorationSpellCasting(spell);
    }

    private void BeginExplorationSpellCasting(SpellDefinition? quickSpell = null)
    {
        var spell = quickSpell;
        MagicItemDefinition? castingItem = null;
        int? castingItemSlotIndex = null;
        var caster = SelectedCharacter;
        if (spell is null)
        {
            var casters = GetSpellcastingPartyMembers();
            if (casters.Count == 0)
            {
                _renderer.DrawInventoryMessage("Senki nem tud varázsolni a partiban.", ConsoleColor.DarkYellow);
                return;
            }
            var startIndex = Math.Max(0, casters.IndexOf(SelectedCharacter));
            var selection = _renderer.DrawSpellCastingScreen(casters, startIndex, inCombat: false, _maze, _fogOfWar,
                GetCasterPosition, ShowInGameHelp);
            _renderer.RestoreSpellCastingOverlay();
            if (selection is null) return;
            spell = selection.Spell;
            caster = selection.Caster;
            castingItem = selection.CastingItem;
            castingItemSlotIndex = selection.CastingItemSlotIndex;
        }
        var result = TryCastSpell(caster, GetCasterPosition(caster), spell, inCombat: false,
            currentEnemy: null, castingItem: castingItem, castingItemSlotIndex: castingItemSlotIndex);
        if (result is not null)
        {
            _renderer.RefreshBattleStatusRows();
            _renderer.DrawInventoryMessage(result.Message, result.Kind == BattleLogKind.Information ? ConsoleColor.Red : ConsoleColor.Magenta);
        }
    }

    private List<LiveCharacter> GetSpellcastingPartyMembers() => CharacterRoster.Party.Members
        .Where(character => character.IsAlive &&
            (character.IsSpellcaster && character.CanCastSpells || EquippedCastingItems(character).Any()))
        .ToList();

    private IEnumerable<MagicItemDefinition> EquippedCastingItems(LiveCharacter character) =>
        character.MagicItems.Select((item, index) => (Item: item, Index: index))
            .Where(entry => entry.Item?.Kind is MagicItemKind.Scroll or MagicItemKind.Wand &&
                entry.Item.SpellId is not null && character.MagicItemCharges[entry.Index] > 0)
            .Where(entry => SpellcastingRules.CanUseCastingItem(character, entry.Item!, _gameData.GetSpell(entry.Item!.SpellId!)))
            .Select(entry => entry.Item!);

    private Position GetCasterPosition(LiveCharacter character) => character == SelectedCharacter
        ? _player.Position
        : _maze.PartyMembers.First(member => member.Character == character).Position;

#endregion

    #region Saving & Loading

            private void SaveGame()
    {
        CancelHeldInventoryItem();
        if (_isReturnExpedition)
        {
            _renderer.DrawInventoryMessage(
                "A visszatérő expedíció közben nem menthetsz. Érd el a régi kijáratot és térj vissza a fogadóba.",
                ConsoleColor.DarkYellow);
            return;
        }
        try
        {
            var path = _gameSaveService.Save(CreateGameSaveData(), CharacterRoster);
            _renderer.DrawDeveloperMessage($"Játék elmentve: {Path.GetFileName(path)}");
            if (_activeCoopHost is not null)
            {
                PublishRemoteCharacterStates(CharacterSyncReason.GameSaved);
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _renderer.DrawDeveloperMessage($"A mentés sikertelen: {exception.Message}");
        }
    }

    private GameSaveData CreateGameSaveData()
    {
        var state = _gameStateMapper.Create(_mazeLevel, _maze, _player, _fogOfWar, _leaderFacing,
            _leaderTrail, _partyHoldingPosition, _partyRegrouping, _partyAttackMode, _hasRestedThisLevel, _partyScatterUntil,
            _nextNeedsDrain, _nextEnemyMoves, _collectedBossKeyIds, _seenBossIds);
        state.QuestJournal = _questJournal.Values.Select(entry => new QuestJournalSaveData(entry.QuestId,
            entry.Status, entry.Progress, entry.ExperienceReward)).ToList();
        state.LocationKind = _locationKind;
        state.LocationId = _locationId;
        state.DifficultyLevel = _difficultyLevel;
        state.SuspendedCampaign = _suspendedCampaignState;
        state.IsCoopGame = _activeCoopHost is not null;
        state.UsedAdHocConversationIds = _usedAdHocConversationIds.ToList();
        state.LastAdHocConversationUtc = _lastAdHocConversationUtc == DateTime.MinValue
            ? null : new DateTimeOffset(_lastAdHocConversationUtc, TimeSpan.Zero);
        state.AdHocConversationMazeLevel = _adHocConversationMazeLevel;
        state.Formation = _formation;
        state.RemoteCharacterIds = _session.CharacterControls
            .Where(control => control.AssignedPlayerId is not null &&
                              control.AssignedPlayerId != _session.HostPlayerId)
            .Select(control => control.CharacterId.Value).ToList();
        return state;
    }

    private void PublishRemoteCharacterStates(CharacterSyncReason reason)
    {
        if (_activeCoopHost is null) return;
        foreach (var control in _session.CharacterControls.Where(control =>
                     control.AssignedPlayerId is not null && control.AssignedPlayerId != _session.HostPlayerId))
        {
            var character = CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == control.CharacterId);
            if (character is not null)
                _activeCoopHost.TryPublishCharacterState(character.Id,
                    _gameSaveService.SerializeCharacter(character), reason);
        }
    }

    private void RestoreGame(GameSaveData state)
    {
        var restored = _gameStateMapper.Restore(state);
        _mazeLevel = restored.MazeLevel;
        _locationKind = state.LocationKind;
        _locationId = string.IsNullOrWhiteSpace(state.LocationId)
            ? $"CAMPAIGN_{_mazeLevel:00}" : state.LocationId;
        _difficultyLevel = state.DifficultyLevel > 0 ? state.DifficultyLevel : _mazeLevel;
        _suspendedCampaignState = state.SuspendedCampaign;
        _collectedBossKeyIds.Clear();
        _collectedBossKeyIds.UnionWith(state.CollectedBossKeyIds ?? []);
        _seenBossIds.Clear();
        _seenBossIds.UnionWith(state.SeenBossIds ?? []);
        _usedAdHocConversationIds.Clear();
        _usedAdHocConversationIds.UnionWith(state.UsedAdHocConversationIds ?? []);
        _lastAdHocConversationUtc = state.LastAdHocConversationUtc?.UtcDateTime ?? DateTime.MinValue;
        _adHocConversationMazeLevel = state.AdHocConversationMazeLevel;
        _questJournal.Clear();
        foreach (var saved in state.QuestJournal ?? [])
            if (_gameData.NpcQuests.FirstOrDefault(quest => string.Equals(quest.Id, saved.QuestId,
                    StringComparison.OrdinalIgnoreCase)) is { } quest)
                _questJournal[quest.Id] = CreateQuestJournalEntry(quest, saved.Status, saved.Progress,
                    saved.ExperienceReward);
        _renderer.SetGoldenKeyCount(_collectedBossKeyIds.Count);
        _maze = restored.Maze;
        CaptureExpeditionEnemyTemplates();
        if (_questJournal.Count == 0)
            foreach (var npc in _maze.WorldNpcs.Concat(_maze.PartyMembers
                         .Where(member => member.TemporaryFollower is not null)
                         .Select(member => member.TemporaryFollower!)))
            foreach (var progress in npc.Quests.Where(progress => progress.State != NpcQuestState.Offered))
                if (_gameData.NpcQuests.FirstOrDefault(quest => string.Equals(quest.Id, progress.QuestId,
                        StringComparison.OrdinalIgnoreCase)) is { } quest)
                    SynchronizeQuestJournal(npc, quest);
        _player = restored.Player;
        _fogOfWar = restored.FogOfWar;
        _leaderFacing = restored.LeaderFacing;
        _formation = PartyFormationRules.Normalize(state.Formation,
            CharacterRoster.Party.Members.Select(member => member.Id), SelectedCharacter.Id);
        _renderer.SetFormationStatus(_formation);
        _session.SetFormationMovementLocked(_formation.State == PartyFormationState.Locked);
        _leaderTrail.Clear();
        _leaderTrail.AddRange(restored.LeaderTrail);
        _partyHoldingPosition = restored.PartyHoldingPosition;
        _partyRegrouping = restored.PartyRegrouping;
        _partyAttackMode = restored.PartyAttackMode;
        _hasRestedThisLevel = restored.HasRestedThisLevel;
        _partyScatterUntil = restored.PartyScatterUntil;
        _nextNeedsDrain = restored.NextNeedsDrain;
        _nextEnemyMoves.Clear();
        foreach (var enemyMove in restored.NextEnemyMoves) _nextEnemyMoves[enemyMove.Key] = enemyMove.Value;
        _nextPartyMoves.Clear();
        foreach (var member in _maze.PartyMembers) ScheduleNextPartyMove(member, DateTime.UtcNow);
        _battleStarted = false;
        _gameOver = false;
        RevealFor(SelectedCharacter, _player.Position);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
        _renderer.DrawDeveloperMessage(_locationKind == AdventureLocationKind.Quest
            ? $"Mentés betöltve: {state.MainCharacterName}, {_maze.LevelName} ({_difficultyLevel}. nehézség)."
            : $"Mentés betöltve: {state.MainCharacterName}, {_mazeLevel}. pálya.");
        _backgroundMusic.SynchronizeMazeLevel(_difficultyLevel, _fogOfWar.IsRevealed(_maze.Exit));
        if (_locationKind == AdventureLocationKind.Quest &&
            string.Equals(_locationId, QuestLocationConfigurations.RodericMalrec, StringComparison.OrdinalIgnoreCase) &&
            FindRodericFollower() is { StoryStateId: "TRUSTED" } legacyRoderic)
            legacyRoderic.SetStoryState("MALREC_APPROACH");
        if (_locationKind == AdventureLocationKind.Quest &&
            string.Equals(_locationId, QuestLocationConfigurations.RodericMalrec, StringComparison.OrdinalIgnoreCase) &&
            FindRodericFollower() is { StoryStateId: "MALREC_DEFEATED" })
            _pendingRodericReturn = true;
        else if (_locationKind == AdventureLocationKind.Campaign && _suspendedCampaignState is null &&
                 FindRodericFollower() is { StoryStateId: "TRUSTED" } roderic &&
                 roderic.Quests.Any(progress =>
                     string.Equals(progress.QuestId, RodericMalrecQuestId, StringComparison.OrdinalIgnoreCase) &&
                     progress.State == NpcQuestState.Offered))
            _pendingRodericExpedition = true;
    }
    #endregion

        private void TryRestParty()
    {
        if (_hasRestedThisLevel)
        {
            _renderer.DrawDeveloperMessage("Ezen a pályán már pihentetek egyszer.");
            return;
        }
        var room = _maze.Rooms.FirstOrDefault(candidate => candidate.Contains(_player.Position));
        if (room is null)
        {
            _renderer.DrawDeveloperMessage("Pihenni csak egy szoba belsejében lehet.");
            return;
        }
        var livingParty = CharacterRoster.Party.Members.Where(character => character.IsAlive).ToList();
        var everyoneInside = livingParty.All(character => character == SelectedCharacter
            ? room.Contains(_player.Position)
            : _maze.PartyMembers.Any(avatar => avatar.Character == character && room.Contains(avatar.Position)));
        if (!everyoneInside)
        {
            _renderer.DrawDeveloperMessage("Pihenéshez minden élő partitag ugyanabban a szobában legyen.");
            return;
        }
        if (_maze.Enemies.Any(enemy => room.Contains(enemy.Position)))
        {
            _renderer.DrawDeveloperMessage("Ellenség van a szobában; itt nem lehet pihenni.");
            return;
        }
        var roomDoors = _maze.Doors.Where(door => room.InteriorPositions()
            .Any(position => Manhattan(position, door.Position) == 1)).ToList();
        if (roomDoors.Count == 0 || roomDoors.Any(door => door.State != DoorState.Locked))
        {
            _renderer.DrawDeveloperMessage("Pihenéshez a szoba minden ajtaját kulcsra kell zárni.");
            return;
        }

        var restResults = new List<CharacterRestSnapshot>();
        foreach (var character in livingParty)
        {
            var beforeVitality = character.CurrentVitality;
            var beforeMana = character.CurrentMana;
            character.RestoreVitality(_random.Next(1, 11));
            character.SetCurrentResources(character.CurrentVitality, character.MaximumMana);
            var cured = new List<string>();
            var cureChance = Math.Clamp(30 + character.EffectiveAbilities.Health * 2, 0, 100);
            foreach (var (statusId, name) in new[]
                     {
                         (CharacterStatusIds.Diseased, "betegség"),
                         (CharacterStatusIds.Poisoned, "mérgezés"),
                         (CharacterStatusIds.Bleeding, "vérzés")
                     })
                if (character.HasStatus(statusId) && _random.Next(100) < cureChance && character.RemoveStatus(statusId))
                    cured.Add($"{_gameData.GetStatus(statusId).Icon} {name}");
            character.ConsumeFood(10);
            character.ConsumeWater(10);
            character.SynchronizeNeedStatuses(_gameData.GetStatus(CharacterStatusIds.Hungry),
                _gameData.GetStatus(CharacterStatusIds.Thirsty));
            restResults.Add(new CharacterRestSnapshot(character.Id, character.Name, character.Color,
                character.CurrentVitality - beforeVitality, character.CurrentMana - beforeMana,
                character.CurrentVitality, character.MaximumVitality, character.CurrentMana, character.MaximumMana,
                character.UsesMana, cured));
        }
        _hasRestedThisLevel = true;
        PlaySessionSound(SoundEffect.Rest);
        ShowSynchronizedRest(new PartyRestSnapshot(Guid.NewGuid(), false, restResults, []));
        TryLogPartyComments(PartySituationIds.Resting);
        PreparePartySpells();
        foreach (var door in roomDoors) _maze.SetDoorState(door, DoorState.Closed);
        _nextNeedsDrain = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        InitializeEnemyMoveSchedule(DateTime.UtcNow);
        foreach (var member in _maze.PartyMembers) ScheduleNextPartyMove(member, DateTime.UtcNow);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
    }

    private void PreparePartySpells()
    {
        foreach (var character in CharacterRoster.Party.Members.Where(character => character.IsAlive && character.IsSpellcaster))
        {
            var control = _session.CharacterControls.FirstOrDefault(candidate => candidate.CharacterId == character.Id);
            if (control is { ControllerKind: CharacterControllerKind.RemotePlayer,
                    ConnectionState: PlayerConnectionState.Connected, AssignedPlayerId: not null })
                WaitForRemoteSpellPreparation(character);
            else
                character.SetMemorizedSpells(_renderer.DrawSpellPreparationScreen(character));
        }
    }

    private void WaitForRemoteSpellPreparation(LiveCharacter character)
    {
        var previousPhase = _session.Phase;
        var spellInfo = SpellInfoSnapshotProjector.Create(character);
        _activeSpellPreparation = new SpellPreparationSnapshot(Guid.NewGuid(), character.Id, character.Name,
            character.MemorizationCapacity, spellInfo.KnownSpells,
            character.MemorizedSpells.Select(spell => spell.Id).ToArray());
        _spellPreparationCompleted = false;
        _session.SetPhase(GameSessionPhase.Paused);
        _renderer.DrawInventoryMessage(
            $"⌛ Várakozás {character.Name} varázsmemorizálására... ⌛", ConsoleColor.Yellow);
        PlaySessionSound(SoundEffect.Waiting, [SelectedCharacter.Id]);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        while (!_spellPreparationCompleted)
        {
            ProcessSessionCommands();
            var stillConnected = _session.CharacterControls.Any(control => control.CharacterId == character.Id &&
                control.ControllerKind == CharacterControllerKind.RemotePlayer &&
                control.ConnectionState == PlayerConnectionState.Connected);
            if (!stillConnected) break;
            if (_activeCoopHost?.ShouldPublish(DateTime.UtcNow) == true)
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            Thread.Sleep(20);
        }
        _activeSpellPreparation = null;
        _spellPreparationCompleted = false;
        _session.SetPhase(previousPhase);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    #region Movement

    private void MovePlayer(Direction direction)
    {
        if (!CanControlledCharacterMove(SelectedCharacter)) return;
        if (_formation.State == PartyFormationState.Locked)
        {
            MoveLockedFormation(direction);
            return;
        }
        var previousPosition = _player.Position;
        var targetPosition = previousPosition + direction;

        // A társak és a követők továbbra sem átjárhatók, de az ütközés nem indít párbeszédet.
        if (_maze.GetObjectAt(targetPosition) is PartyMemberAvatar) return;
        if (_maze.GetEnemyAt(targetPosition) is { } encounteredEnemy)
        {
            StartBattle(encounteredEnemy);
            return;
        }
        if (_maze.GetWorldNpcAt(targetPosition) is { } npc)
        {
            if (!EncounterWorldNpc(npc)) return;
        }
        if (!CanEnterTrap(SelectedCharacter, targetPosition)) return;

        var moved = _player.TryMove(direction, _maze);
        if (!moved)
        {
            if (_developerPhasing && _maze.IsInside(targetPosition))
            {
                // Destroy wall/door and move through
                _maze.RemoveDoor(targetPosition);
                _maze.Carve(targetPosition);
                _player.TeleportTo(targetPosition);
            }
            else
            {
                return;
            }
        }
        SelectedCharacter.RegisterExplorationStep();
        ScheduleNextControlledMove(SelectedCharacter);
        _leaderFacing = direction;
        if (_leaderTrail[^1] != _player.Position) _leaderTrail.Add(_player.Position);
        if (_leaderTrail.Count > 256) _leaderTrail.RemoveRange(0, _leaderTrail.Count - 256);

        var newlyRevealed = RevealFor(SelectedCharacter, _player.Position, advanceEnemyMemory: true);
        var justReachedExit = _player.Position == _maze.Exit && previousPosition != _maze.Exit;
        _renderer.DrawMovement(_maze, _fogOfWar, previousPosition, _player.Position, newlyRevealed, justReachedExit);
        CheckBossDiscoveryAt(newlyRevealed, SelectedCharacter);
        PlayCharacterStepSound(SelectedCharacter);
        CollectTreasureChest(SelectedCharacter, _player.Position, shareLootWithParty: true);
        TriggerTrapAt(SelectedCharacter, _player.Position);
        var enemy = _maze.GetEnemyAt(_player.Position);
        if (enemy is not null) StartBattle(enemy);
    }

    private void MoveRemotePartyMember(MoveCharacterCommand command)
    {
        var member = _maze.PartyMembers.FirstOrDefault(candidate => candidate.Character.Id == command.CharacterId);
        if (member is null || !member.Character.IsAlive) return;
        if (!CanControlledCharacterMove(member.Character)) return;
        var previous = member.Position;
        var destination = previous + command.Direction;
        if (_maze.GetEnemyAt(destination) is { } enemy)
        {
            StartBattle(member, enemy);
            return;
        }
        if (!CanEnterTrap(member.Character, destination)) return;
        if (!_maze.TryMovePartyMember(member, destination, _player.Position, allowTreasureChest: true)) return;
        member.Character.RegisterExplorationStep();
        ScheduleNextControlledMove(member.Character);
        var newlyRevealed = RevealFor(member.Character, member.Position, advanceEnemyMemory: true);
        _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, previous, member.Position, newlyRevealed, _player.Position);
        PlayCharacterStepSound(member.Character);
        CheckBossDiscoveryAt(newlyRevealed, member.Character);
        CollectTreasureChest(member.Character, member.Position, shareLootWithParty: false);
        TriggerTrapAt(member.Character, member.Position);
    }

    #endregion

        private void CollectTreasureChest(LiveCharacter character, Position position, bool shareLootWithParty)
    {
        var chest = _maze.GetTreasureChestAt(position);
        if (chest is null) return;
        var rules = _gameData.LootRules;
        var jackpotChance = AdjustedSearchChance(character, rules.ChestJackpotChancePercent);
        var jackpot = _random.Next(100) < jackpotChance;
        var rewardMultiplier = jackpot ? rules.ChestJackpotMultiplier : 1;
        if (character.HasPerk(PerkIds.ThiefMasterThief)) rewardMultiplier *= 2;
        var goldAmount = chest.GoldAmount * rewardMultiplier;
        SelectedCharacter.AddGold(goldAmount);
        var masterThiefLoot = RollMasterThiefChestLoot(character);
        _maze.RemoveTreasureChest(chest);
        RegisterNpcQuestProgress(NpcQuestType.OpenChest, "ANY");
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        if (character == SelectedCharacter)
            _renderer.DrawTreasureCollected(goldAmount, jackpot, jackpotChance, rewardMultiplier);

        var message = $"🎁 {character.Name} kinyitotta a kincsesládát: {goldAmount} arany" +
                      (jackpot ? $" (jackpot, {jackpotChance}% esély)" : string.Empty) + ".";
        _renderer.DrawInventoryMessage(message, jackpot ? ConsoleColor.Magenta : ConsoleColor.Yellow);
        RecordSessionActivity(SessionActivityKind.System, message,
            jackpot ? ConsoleColor.Magenta : ConsoleColor.Yellow, [character.Id]);
        PlaySessionSound(jackpot ? SoundEffect.Chest2 : SoundEffect.Chest, [character.Id]);

        if (masterThiefLoot is null) return;
        if (TryStoreSearchedLoot(character, masterThiefLoot, shareLootWithParty, out var owner))
            message = $"🎁 Mestertolvaj: {masterThiefLoot.Name} → {owner} hátizsákja.";
        else
        {
            _maze.DropItem(position, masterThiefLoot);
            message = $"🎁 Mestertolvaj: {masterThiefLoot.Name} a földön maradt, mert a hátizsák tele van.";
        }
        _renderer.DrawInventoryMessage(message, ConsoleColor.Magenta);
        RecordSessionActivity(SessionActivityKind.System, message, ConsoleColor.Magenta, [character.Id]);
    }

    private void SubmitLocalExplorationCommand(ConsoleKeyInfo keyInfo)
    {
        GameCommand? command = null;
        var commandId = _localCommandId + 1;
        var key = keyInfo.Key;
        if ((keyInfo.Modifiers & ConsoleModifiers.Control) != 0 &&
            key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
            command = new LeaderActionCommand(_session.HostPlayerId, commandId, SelectedCharacter.Id,
                key == ConsoleKey.LeftArrow ? LeaderAction.RotateFormationLeft : LeaderAction.RotateFormationRight);
        else if (TryGetDirection(key, out var direction))
            command = new MoveCharacterCommand(_session.HostPlayerId, commandId, SelectedCharacter.Id, direction);
        else if (GameInputBindings.CharacterAction(key) is { } characterAction)
        {
            Position? targetDoor = null;
            if (characterAction is CharacterAction.OpenDoor or CharacterAction.CloseOrLockDoor)
            {
                var doors = AdjacentDoorPositions(SelectedCharacter, _player.Position, includeFormation: true);
                if (doors.Count == 1) targetDoor = doors[0];
                else if (doors.Count > 1)
                {
                    targetDoor = SelectDoorTarget(doors, characterAction);
                    if (targetDoor is null) return;
                }
            }
            var keyChoice = GetLocalThiefKeyChoice(characterAction, targetDoor);
            command = new CharacterActionCommand(_session.HostPlayerId, commandId, SelectedCharacter.Id,
                characterAction, targetDoor, keyChoice.UseKey, keyChoice.KeyOwnerCharacterId);
        }
        else
        {
            var action = GameInputBindings.LeaderAction(key, _player.Position == _maze.Exit);
            if (action is not null)
                command = new LeaderActionCommand(_session.HostPlayerId, commandId, SelectedCharacter.Id, action.Value);
        }
        if (command is null || !_session.Submit(command)) return;
        _localCommandId = commandId;
    }

    #region Session & Networking

    private void ProcessSessionCommands() => _commandDispatcher.ProcessPendingCommands();

    void ISessionCommandHandler.OnSetHelpVisibility(PlayerId senderId, CharacterId characterId, bool isOpen) =>
        SetHelpVisibility(senderId, characterId, isOpen);

    bool ISessionCommandHandler.IsPausedByHelp() => _helpPausePlayers.Count > 0;

    void ISessionCommandHandler.OnMoveLeader(Direction direction) => MovePlayer(direction);

    void ISessionCommandHandler.OnMoveRemoteMember(MoveCharacterCommand command) => MoveRemotePartyMember(command);

    void ISessionCommandHandler.OnCharacterAction(CharacterActionCommand command) => ExecuteCharacterAction(command);

    void ISessionCommandHandler.OnLeaderAction(LeaderAction action) => ExecuteLeaderAction(action);

    void ISessionCommandHandler.OnInventoryTransfer(InventoryTransferCommand command) => ExecuteInventoryTransfer(command);

    void ISessionCommandHandler.OnUseInventoryItem(UseInventoryItemCommand command) => ExecuteUseInventoryItem(command);

    void ISessionCommandHandler.OnDropInventoryItem(DropInventoryItemCommand command) => ExecuteDropInventoryItem(command);

    void ISessionCommandHandler.OnSplitInventoryStack(SplitInventoryStackCommand command) => ExecuteSplitInventoryStack(command);

    void ISessionCommandHandler.OnDistributeInventoryStack(DistributeInventoryStackCommand command) =>
        ExecuteDistributeInventoryStack(command);

    void ISessionCommandHandler.OnGiveFollowerStack(GiveFollowerStackCommand command) => ExecuteGiveFollowerStack(command);

    void ISessionCommandHandler.OnPickUpGroundItem(PickUpGroundItemCommand command) => ExecutePickUpGroundItem(command);

    void ISessionCommandHandler.OnBattleAction(BattleActionCommand command) => ExecuteBattleAction(command);

    void ISessionCommandHandler.OnCastExplorationSpell(CastExplorationSpellCommand command) => ExecuteExplorationSpell(command);

    void ISessionCommandHandler.OnInnPurchase(InnPurchaseCommand command) => ExecuteInnPurchase(command);

    void ISessionCommandHandler.OnInnSale(InnSaleCommand command) => ExecuteInnSale(command);

    void ISessionCommandHandler.OnAcknowledgeNarrative(AcknowledgeNarrativeCommand command) =>
        ExecuteNarrativeAcknowledgement(command);

    void ISessionCommandHandler.OnAcknowledgeLevelImage(AcknowledgeLevelImageCommand command) =>
        ExecuteLevelImageAcknowledgement(command);

    void ISessionCommandHandler.OnAcknowledgeRest(AcknowledgeRestCommand command) => ExecuteRestAcknowledgement(command);

    void ISessionCommandHandler.OnAssignQuickSpell(AssignQuickSpellCommand command) => ExecuteAssignQuickSpell(command);

    void ISessionCommandHandler.OnPrepareSpells(PrepareSpellsCommand command) => ExecuteSpellPreparation(command);

    void ISessionCommandHandler.OnResolveLevelUpPrompt(ResolveLevelUpPromptCommand command) => ExecuteLevelUpPrompt(command);

#endregion

    private void ExecuteInnPurchase(InnPurchaseCommand command)
    {
        var recipient = CharacterRoster.Party.Members.FirstOrDefault(character => character.Id == command.CharacterId);
        if (recipient is null)
        {
            _session.RejectExecutedCommand(command, "A vásárló karakter már nem tagja a partinak.");
            return;
        }
        if (!_innController.TryPurchase(command.Vendor, command.OfferIndex, command.ExpectedInnRevision,
                recipient, out var message))
            _session.RejectExecutedCommand(command, message);
    }

    private void ExecuteInnSale(InnSaleCommand command)
    {
        var seller = CharacterRoster.Party.Members.FirstOrDefault(character => character.Id == command.CharacterId);
        if (seller is null)
        {
            _session.RejectExecutedCommand(command, "Az eladó karakter már nem tagja a partinak.");
            return;
        }
        if (!_innController.TrySell(command.ExpectedInnRevision, command.ExpectedInventoryRevision,
                command.BackpackIndex, seller, out var message))
            _session.RejectExecutedCommand(command, message);
    }

    private ConsoleKeyInfo ReadInnKey()
    {
        var key = ReadInnKeyCore();
        if (key.Key == ConsoleKey.Q)
        {
            ShowQuestJournal();
            return new ConsoleKeyInfo('\0', InnController.StateChangedKey, false, false, false);
        }
        if (GameInputBindings.IsCharacterSheetToggle(key.Key))
        {
            ManageCharacterSheetAtInn();
            return new ConsoleKeyInfo('\0', InnController.StateChangedKey, false, false, false);
        }
        return key;
    }

    private ConsoleKeyInfo ReadInnKeyCore()
    {
        var initialRevision = _innController.Revision;
        while (!Console.KeyAvailable)
        {
            ProcessSessionCommands();
            if (_innController.Revision != initialRevision)
            {
                _activeCoopHost?.TryPublish(CreateSessionSnapshot());
                return new ConsoleKeyInfo('\0', InnController.StateChangedKey, false, false, false);
            }
            if (_activeCoopHost?.ShouldPublish(DateTime.UtcNow) == true)
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            Thread.Sleep(20);
        }
        return Console.ReadKey(intercept: true);
    }

    private void ManageCharacterSheetAtInn()
    {
        CancelHeldInventoryItem();
        _characterSheetFocused = true;
        _renderer.DrawInnCharacterSheet(SelectedCharacter);
        while (true)
        {
            var keyInfo = ReadInnKeyCore();
            if (keyInfo.Key == InnController.StateChangedKey)
            {
                _renderer.RefreshCharacterSheet(SelectedCharacter);
                continue;
            }
            if (GameInputBindings.IsCharacterSheetToggle(keyInfo.Key) || keyInfo.Key == ConsoleKey.Escape)
            {
                CancelHeldInventoryItem();
                _characterSheetFocused = false;
                _renderer.SetCharacterSheetFocused(false);
                return;
            }
            if (keyInfo.Key == ConsoleKey.Q)
            {
                ShowQuestJournal();
                continue;
            }
            switch (GameInputBindings.InventoryAction(keyInfo.Key))
            {
                case InventoryInputAction.MoveUp: _renderer.MoveCharacterSheetSelection(-1); break;
                case InventoryInputAction.MoveDown: _renderer.MoveCharacterSheetSelection(1); break;
                case InventoryInputAction.Inspect: InspectSelectedInventoryItem(); break;
                case InventoryInputAction.Use: UseSelectedInventoryItem(); break;
                case InventoryInputAction.MoveItem: GrabOrPlaceInventoryItem(); break;
                case InventoryInputAction.SplitStack: SplitSelectedInventoryStack(); break;
                case InventoryInputAction.DistributeStack: DistributeSelectedInventoryStack(); break;
                case InventoryInputAction.CharacterDetails: ShowCharacterDetails(); break;
                case InventoryInputAction.GiveFollowerStack: GiveSelectedStackToFollower(); break;
                case InventoryInputAction.Drop:
                    _renderer.DrawInventoryMessage("A fogadóban nem dobhatsz tárgyat a földre.", ConsoleColor.DarkYellow);
                    break;
                default:
                    if (keyInfo.Key == ConsoleKey.LeftArrow) _renderer.MoveDisplayedPartyMember(-1);
                    else if (keyInfo.Key == ConsoleKey.RightArrow) _renderer.MoveDisplayedPartyMember(1);
                    else if (keyInfo.Key == ConsoleKey.Delete) DismissSelectedPartyMember();
                    break;
            }
        }
    }

    private void ExecuteNarrativeAcknowledgement(AcknowledgeNarrativeCommand command)
    {
        if (_activeNarrative?.NarrativeId != command.NarrativeId)
        {
            _session.RejectExecutedCommand(command, "Ez a történeti ablak már nem aktív.");
            return;
        }
        _narrativeAcknowledgements.Add(command.SenderId);
        PlaySessionSound(SoundEffect.Waiting, [command.CharacterId]);
    }

    private void ExecuteLevelImageAcknowledgement(AcknowledgeLevelImageCommand command)
    {
        if (_activeLevelImage?.ImageId != command.ImageId)
        {
            _session.RejectExecutedCommand(command, "Ez a pályakép már nem aktív.");
            return;
        }
        AcknowledgeLevelImage(command.SenderId, command.CharacterId);
    }

    private bool EncounterWorldNpc(WorldNpc npc)
    {
        if (!npc.CanStartConversation) return false;

        var definition = npc.DefinitionId == "NPC-FIRST-COMPANION"
            ? new NpcDefinition(
                "NPC-FIRST-COMPANION",
                npc.Character.Name,
                "na",
                NpcDisposition.Friendly,
                NpcWorldBehavior.Friendly,
                true,
                false)
            : _gameData.GetNpc(npc.DefinitionId);
        if (definition.Unique && string.Equals(definition.StoryId, EliraStoryId, StringComparison.OrdinalIgnoreCase))
        {
            ConverseWithFirstUniqueNpc(npc);
            return false;
        }
        if (definition.Unique && string.Equals(definition.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase))
        {
            ConverseWithRoderic(npc);
            return false;
        }
        if (definition.Unique)
        {
            _renderer.DrawUniqueNpcIntroduction(npc);
            _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
            return false;
        }
        var questDefinitions = _gameData.GetNpcQuests(npc.DefinitionId);
        var result = _renderer.DrawWorldNpcRecruitment(npc, questDefinitions);
        ProcessNpcQuests(npc);
        if (result == WorldNpcInteractionResult.Continue)
        {
            _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
            return true;
        }
        if (result == WorldNpcInteractionResult.Join && CharacterRoster.Party.Add(npc.Character))
        {
            npc.Character.SetNpcJoinOrigin(_mazeLevel, "A pályán csatlakozott");
            _maze.RemoveWorldNpc(npc);
            var avatar = new PartyMemberAvatar(npc.Position, npc.Character);
            _maze.AddPartyMember(avatar);
            _nextPartyMoves[avatar] = DateTime.UtcNow;
            RevealFor(npc.Character, avatar.Position);
            _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
            _renderer.DrawInventoryMessage($"🤝 {npc.Character.Name} ingyen csatlakozott a partihoz.", ConsoleColor.Green);
            _activeCoopHost?.TryPublish(CreateSessionSnapshot());
            return false;
        }

        npc.Decline();
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
        _renderer.DrawInventoryMessage(result == WorldNpcInteractionResult.Join ? "A parti megtelt; előbb helyet kell felszabadítani."
            : $"{npc.Character.Name} egyelőre itt marad.", ConsoleColor.Yellow);
        return false;
    }

    private void ConverseWithRoderic(WorldNpc npc)
    {
        if (string.Equals(npc.StoryStateId, "PROOF_ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            var quest = _gameData.NpcQuests.Single(value =>
                string.Equals(value.Id, RodericGraveRespectQuestId, StringComparison.OrdinalIgnoreCase));
            var progress = npc.Quests.Single(value =>
                string.Equals(value.QuestId, quest.Id, StringComparison.OrdinalIgnoreCase));
            if (progress.Progress < quest.RequiredCount)
            {
                _renderer.DrawUniqueNpcStoryChoice(npc,
                    $"Négy feltámasztott csontváz járja a közeli kriptákat. Eddig {progress.Progress}/4 bukott el.",
                    ["A sírokhoz nem nyúlunk. Visszatérünk ha végeztünk."]);
                _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
                return;
            }

            ProcessNpcQuests(npc, activateOffered: false);
            npc.SetStoryState("PROOF_COMPLETE");
            RunStoryConversation(npc);
            _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
            return;
        }

        if (string.Equals(npc.StoryStateId, "INSIGNIAS_ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            var count = CountPartyBackpackItems(MiscItemIds.FallenKnightInsignia);
            if (count < 3)
            {
                _renderer.DrawUniqueNpcStoryChoice(npc,
                    $"Három jelvényt keressetek. Eddig {count}/3 került elő.", ["Folytatjuk a keresést."]);
                _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
                return;
            }

            ProcessNpcQuests(npc, activateOffered: false);
            npc.SetStoryState("CONFESSION");
            RunStoryConversation(npc);
            _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
            return;
        }

        if (_gameData.GetNpcStoryChoices(npc.StoryId ?? string.Empty, npc.StoryStateId,
                npc.Friendliness).Count > 0)
        {
            RunStoryConversation(npc);
            if (string.Equals(npc.StoryStateId, "JOIN_ACCEPTED", StringComparison.OrdinalIgnoreCase))
                TryFinalizeRodericPermanentJoin();
            if (string.Equals(npc.StoryStateId, "MALREC_APPROACH", StringComparison.OrdinalIgnoreCase))
                _pendingRodericExpedition = true;
            _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
            return;
        }
        _renderer.DrawUniqueNpcIntroduction(npc);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
    }

    private void RunStoryConversation(WorldNpc npc)
    {
        var transcript = new List<string>();
        while (true)
        {
            var choices = _gameData.GetNpcStoryChoices(npc.StoryId ?? string.Empty, npc.StoryStateId,
                npc.Friendliness);
            if (choices.Count == 0) return;
            var index = _renderer.DrawUniqueNpcStoryChoice(npc, choices[0].Prompt,
                choices.Select(choice => choice.Text).ToArray(), transcript);
            var selected = choices[index];
            transcript.Add($"Te: {selected.Text}");
            transcript.AddRange(selected.Response.Split('|',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            npc.AdjustFriendliness(selected.FriendlinessChange);
            npc.SetStoryState(selected.NextStateId);
            if (!selected.ContinueConversation)
            {
                _renderer.DrawUniqueNpcStoryResponse(npc, transcript);
                ApplyNpcStoryAction(npc, selected);
                return;
            }
            ApplyNpcStoryAction(npc, selected);
        }
    }

    private void ApplyNpcStoryAction(WorldNpc npc, NpcStoryChoiceDefinition choice)
    {
        switch (choice.Action)
        {
            case NpcStoryAction.None:
            case NpcStoryAction.TravelToLocation:
            case NpcStoryAction.RequestPermanentJoin:
                return;
            case NpcStoryAction.ActivateQuest when choice.ActionParameter is { } questId:
                ActivateNpcQuest(npc, questId);
                return;
            case NpcStoryAction.BeginFollowing:
                if (!BeginTemporaryFollowing(npc))
                {
                    npc.SetStoryState(choice.StateId);
                    npc.AdjustFriendliness(-choice.FriendlinessChange);
                }
                return;
            case NpcStoryAction.GrantEmergencySupplies:
                GrantRodericEmergencySupplies(npc);
                return;
            default:
                throw new InvalidOperationException($"Hiányos NPC-történeti hatás: {choice.Id}/{choice.Action}.");
        }
    }

    private void ActivateNpcQuest(WorldNpc npc, string questId)
    {
        if (!npc.ActivateQuest(questId)) return;
        var quest = _gameData.NpcQuests.First(value =>
            string.Equals(value.Id, questId, StringComparison.OrdinalIgnoreCase));
        SynchronizeQuestJournal(npc, quest);
        _renderer.DrawInventoryMessage($"📜 Új küldetés: {quest.Title} — {quest.Description} " +
            $"Jutalom: {quest.ExperienceReward} XP.", ConsoleColor.Cyan);
    }

    private void GrantRodericEmergencySupplies(WorldNpc npc)
    {
        var bundle = new[]
        {
            new InventoryBundleEntry(_gameData.GetItem("T012"), 4),
            new InventoryBundleEntry(_gameData.GetItem("T004"), 2),
            new InventoryBundleEntry(_gameData.GetItem("T006"), 2),
            new InventoryBundleEntry(_gameData.GetItem("T002"), 4)
        };
        if (!InventoryBundleGrantService.TryGrant(CharacterRoster.Party.Members, bundle, out var lackingSpace))
        {
            npc.SetStoryState("CACHE_BLOCKED");
            _renderer.DrawInventoryMessage(
                $"Az Ezüst Eskü készlete érintetlen maradt. Előbb hátizsákhely kell: {string.Join(", ", lackingSpace)}.",
                ConsoleColor.DarkYellow);
            return;
        }

        _renderer.DrawInventoryMessage(
            $"🗝 Az Ezüst Eskü vésztartaléka kiosztva {CharacterRoster.Party.Members.Count} partitag között: " +
            "fejenként 4 gyógyital, 2 kenyér, 2 füstölt hús és 4 bőrkulacs.", ConsoleColor.Green);
    }

    private void ConverseWithFirstUniqueNpc(WorldNpc npc)
    {
        if (npc.ConversationStage == 0)
        {
            var sameRaceMembers = CharacterRoster.Party.Members.Count(character =>
                string.Equals(character.Race.Id, npc.Character.Race.Id, StringComparison.OrdinalIgnoreCase));
            var affinity = string.Equals(SelectedCharacter.Race.Id, npc.Character.Race.Id,
                StringComparison.OrdinalIgnoreCase) ? 2 : sameRaceMembers > 0 ? 1 : 0;
            if (affinity > 0)
            {
                npc.AdjustFriendliness(affinity);
                _renderer.DrawInventoryMessage($"🌿 Faji rokonszenv: Elira viszonya +{affinity}.", ConsoleColor.Green);
            }
        }

        var result = _renderer.DrawUniqueNpcConversation(npc);
        var friendlinessChange = result.FriendlinessChange;
        if (npc.ConversationStage == 2 && result.ChoiceIndex == 1 &&
            !CharacterRoster.Party.Members.Any(character => string.Equals(character.Race.Id,
                npc.Character.Race.Id, StringComparison.OrdinalIgnoreCase))) friendlinessChange = -1;
        npc.AdjustFriendliness(friendlinessChange);
        if (result.ChoiceIndex >= 0) npc.AdvanceConversation();
        if (result.FollowRequested && npc.State != WorldNpcState.Following)
            BeginTemporaryFollowing(npc);
        if (npc.State == WorldNpcState.Following) ProcessNpcQuests(npc);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
        _renderer.DrawInventoryMessage($"🌿 Elira viszonya: {npc.Friendliness}/10.",
            friendlinessChange >= 0 ? ConsoleColor.Green : ConsoleColor.DarkYellow);
    }

    private bool BeginTemporaryFollowing(WorldNpc npc)
    {
        if (_maze.PartyMembers.Any(member => member.IsTemporaryFollower))
        {
            _renderer.DrawInventoryMessage("Már van egy ideiglenes követőtök.", ConsoleColor.DarkYellow);
            return false;
        }
        if (!_maze.RemoveWorldNpc(npc)) return false;
        npc.BeginFollowing();
        var newlyActivated = new List<NpcQuestDefinition>();
        foreach (var quest in npc.Quests.Where(progress => progress.State == NpcQuestState.Offered).ToArray())
        {
            var definition = _gameData.NpcQuests.First(value =>
                string.Equals(value.Id, quest.QuestId, StringComparison.OrdinalIgnoreCase));
            if (definition.RequiredStoryStateId is { } requiredState &&
                !string.Equals(requiredState, npc.StoryStateId, StringComparison.OrdinalIgnoreCase)) continue;
            npc.ActivateQuest(quest.QuestId);
            SynchronizeQuestJournal(npc, definition);
            newlyActivated.Add(definition);
        }
        var avatar = new PartyMemberAvatar(npc.Position, npc.Character, npc);
        _maze.AddPartyMember(avatar);
        _nextPartyMoves[avatar] = DateTime.UtcNow;
        if (string.Equals(npc.StoryId, EliraStoryId, StringComparison.OrdinalIgnoreCase))
            _renderer.DrawUniqueNpcQuestOffer(npc, _gameData.GetNpcQuests(npc.DefinitionId));
        else if (newlyActivated.Count > 0)
            _renderer.DrawGenericUniqueNpcQuestOffer(npc, newlyActivated);
        _renderer.DrawInventoryMessage($"🌿 {npc.Character.Name} ideiglenes követőként csatlakozott. Nem foglal partyhelyet.",
            ConsoleColor.Cyan);
        return true;
    }

    private void ProcessNpcQuests(WorldNpc npc, bool activateOffered = true)
    {
        foreach (var progress in npc.Quests.Where(quest => quest.State != NpcQuestState.Completed).ToArray())
        {
            var quest = _gameData.NpcQuests.First(value =>
                string.Equals(value.Id, progress.QuestId, StringComparison.OrdinalIgnoreCase));
            if (progress.State == NpcQuestState.Offered)
            {
                if (!activateOffered) continue;
                if (quest.RequiredStoryStateId is { } requiredState &&
                    !string.Equals(requiredState, npc.StoryStateId, StringComparison.OrdinalIgnoreCase)) continue;
                npc.ActivateQuest(quest.Id);
                SynchronizeQuestJournal(npc, quest);
                _renderer.DrawInventoryMessage($"📜 Új küldetés: {quest.Title} — {quest.Description} " +
                    $"Jutalom: {quest.ExperienceReward} XP{DescribeNpcQuestItemRewards(quest)}.", ConsoleColor.Cyan);
            }

            var current = npc.Quests.First(value => string.Equals(value.QuestId, quest.Id,
                StringComparison.OrdinalIgnoreCase));
            if (quest.Type == NpcQuestType.Collect)
            {
                var available = CountPartyBackpackItems(quest.TargetId);
                if (available < quest.RequiredCount)
                {
                    _renderer.DrawInventoryMessage(
                        $"📜 {quest.Title}: {available}/{quest.RequiredCount}", ConsoleColor.DarkYellow);
                    SynchronizeQuestJournal(npc, quest, available);
                    continue;
                }
                RemovePartyBackpackItems(quest.TargetId, quest.RequiredCount);
                npc.AddQuestProgress(quest.Id, quest.RequiredCount, quest.RequiredCount);
            }
            else if (current.Progress < quest.RequiredCount)
            {
                SynchronizeQuestJournal(npc, quest);
                _renderer.DrawInventoryMessage(
                    $"📜 {quest.Title}: {current.Progress}/{quest.RequiredCount}", ConsoleColor.DarkYellow);
                continue;
            }

            if (!npc.CompleteQuest(quest.Id)) continue;
            SynchronizeQuestJournal(npc, quest);
            if (_gameData.GetNpc(npc.DefinitionId).Unique)
                npc.AdjustFriendliness(1);
            var awards = DistributeExperience(SelectedCharacter, quest.ExperienceReward);
            var leveledAwards = awards.Where(award => award.Result.LeveledUp && award.Character.IsAlive).ToArray();
            foreach (var award in leveledAwards)
                ResolvePerkOffers(award.Character, award.Result);
            if (leveledAwards.Length > 0)
                _renderer.RefreshCharacterSheet(SelectedCharacter);
            var itemRewards = GrantNpcQuestItems(quest);
            _renderer.DrawInventoryMessage(
                $"✅ Küldetés teljesítve: {quest.Title}. XP: {FormatExperienceAwards(awards)}." +
                (itemRewards.Length > 0 ? $" 🎁 {itemRewards}" : string.Empty), ConsoleColor.Green);
        }
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private void ShowQuestJournal()
    {
        QuestJournalWindow.Show(OrderedQuestJournal());
    }

    private void ShowCharacterDetails()
    {
        CharacterDetailsWindow.Show(CreateCharacterDetailsSnapshot(_renderer.DisplayedCharacter), _gameData);
    }

    private IReadOnlyList<QuestJournalEntrySnapshot> OrderedQuestJournal() =>
        NpcQuestCoordinator.OrderedQuestJournal(_questJournal.Values);

    private void SynchronizeQuestJournal(WorldNpc npc, NpcQuestDefinition quest, int? visibleProgress = null) =>
        _npcQuestCoordinator.SynchronizeQuestJournal(_questJournal, npc, quest, visibleProgress);

    private QuestJournalEntrySnapshot CreateQuestJournalEntry(NpcQuestDefinition quest,
        QuestJournalStatus status, int progress, int experienceReward) =>
        _npcQuestCoordinator.CreateQuestJournalEntry(quest, status, progress, experienceReward);

    private string GrantNpcQuestItems(NpcQuestDefinition quest)
    {
        var rewards = new List<IItemDefinition>();
        if (quest.RewardItemId is { } fixedItemId)
        {
            var fixedItem = FindQuestRewardItem(fixedItemId);
            for (var count = 0; count < quest.RewardItemCount; count++) rewards.Add(fixedItem);
        }
        for (var count = 0; count < quest.RandomRewardCount; count++)
            if (RollQuestReward(quest.ExperienceReward) is { } reward) rewards.Add(reward);
        if (rewards.Count == 0) return string.Empty;

        var dropped = 0;
        foreach (var reward in rewards)
        {
            if (TryStoreLootInParty(reward, out _)) continue;
            _maze.DropItem(_player.Position, reward);
            dropped++;
        }
        PlaySessionSound(SoundEffect.Item);
        var summary = string.Join(", ", rewards.GroupBy(item => item.Name)
            .Select(group => $"{group.Key} ×{group.Count()}"));
        return dropped == 0 ? summary : $"{summary} ({dropped} a földön)";
    }

    private string DescribeNpcQuestItemRewards(NpcQuestDefinition quest)
    {
        var parts = new List<string>();
        if (quest.RewardItemId is { } fixedItemId)
            parts.Add($"{FindQuestRewardItem(fixedItemId).Name} ×{quest.RewardItemCount}");
        if (quest.RandomRewardCount > 0) parts.Add($"{quest.RandomRewardCount} véletlen tárgy");
        return parts.Count == 0 ? string.Empty : " + " + string.Join(" + ", parts);
    }

    private IItemDefinition? RollQuestReward(int experienceReward)
    {
        var maximumRarity = experienceReward >= 2000 ? ItemRarity.Legendary :
            experienceReward >= 800 ? ItemRarity.Magic : ItemRarity.Normal;
        var maximumPrice = Math.Max(80, experienceReward * 2);
        var maximumMagicPower = Math.Max(0, experienceReward / 300);
        var candidates = QuestRewardItems().Where(item => item.Rarity <= maximumRarity &&
            item.BasePrice <= maximumPrice && item.MagicPower <= maximumMagicPower).ToArray();
        return candidates.Length == 0 ? null : candidates[_random.Next(candidates.Length)];
    }

    private IItemDefinition FindQuestRewardItem(string itemId) => _gameData.GetItemDefinition(itemId);

    private IEnumerable<IItemDefinition> QuestRewardItems() => _gameData.Items.Cast<IItemDefinition>()
        .Concat(_gameData.Weapons).Concat(_gameData.Armors).Concat(_gameData.MagicItems)
        .Where(item => !SpellcastingRules.IsRestrictedFromTradingAndGeneration(item));

    private int CountPartyBackpackItems(string itemId) => CharacterRoster.Party.Members.Sum(character =>
        Enumerable.Range(0, LiveCharacter.MaximumBackpackItemCount)
            .Where(index => string.Equals(character.Backpack[index]?.Id, itemId, StringComparison.OrdinalIgnoreCase))
            .Sum(index => character.GetInventoryItemQuantity(InventorySlotKind.Backpack, index)));

    private void RemovePartyBackpackItems(string itemId, int count)
    {
        var remaining = count;
        foreach (var character in CharacterRoster.Party.Members)
        for (var index = 0; index < LiveCharacter.MaximumBackpackItemCount && remaining > 0; index++)
            while (remaining > 0 && string.Equals(character.Backpack[index]?.Id, itemId,
                       StringComparison.OrdinalIgnoreCase) &&
                   character.RemoveOneInventoryItem(InventorySlotKind.Backpack, index)) remaining--;
    }

    private void RegisterNpcQuestKill(Enemy defeatedEnemy)
    {
        var enemyDefinitionId = defeatedEnemy.Definition.Id;
        var isMalrec = string.Equals(enemyDefinitionId, MonsterIds.SirMalrec,
            StringComparison.OrdinalIgnoreCase);
        var rodericAtConfrontation = isMalrec &&
            FindRodericFollower() is { StoryStateId: "MALREC_FIGHT" } witness
                ? witness
                : null;
        if (!isMalrec || rodericAtConfrontation is not null)
            RegisterNpcQuestProgress(NpcQuestType.Kill, enemyDefinitionId);
        var enemy = defeatedEnemy.Definition;
        foreach (var avatar in _maze.PartyMembers.Where(member => member.TemporaryFollower is not null &&
                     member.Character.IsAlive && Manhattan(member.Position, defeatedEnemy.Position) <= 6))
        {
            var npc = avatar.TemporaryFollower!;
            foreach (var progress in npc.Quests.Where(value => value.State == NpcQuestState.Active).ToArray())
            {
                var quest = _gameData.NpcQuests.First(value =>
                    string.Equals(value.Id, progress.QuestId, StringComparison.OrdinalIgnoreCase));
                if (quest.Type != NpcQuestType.KillWithFollower ||
                    !enemy.MatchesAbilityOrLegacyTrait(quest.TargetId)) continue;
                npc.AddQuestProgress(quest.Id, 1, quest.RequiredCount);
                SynchronizeQuestJournal(npc, quest);
                var updated = npc.Quests.First(value =>
                    string.Equals(value.QuestId, quest.Id, StringComparison.OrdinalIgnoreCase));
                if (updated.Progress < quest.RequiredCount) continue;
                ProcessNpcQuests(npc, activateOffered: false);
                if (string.Equals(npc.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(quest.Id, RodericSharedBattleQuestId, StringComparison.OrdinalIgnoreCase))
                {
                    npc.SetStoryState("TRUSTED");
                    _renderer.DrawInventoryMessage("⚜ Roderic most először valódi bajtársakként tekint rátok.",
                        ConsoleColor.Cyan);
                    _pendingRodericExpedition = true;
                }
            }
        }

        if (!isMalrec || rodericAtConfrontation is null) return;
        ProcessNpcQuests(rodericAtConfrontation, activateOffered: false);
        rodericAtConfrontation.SetStoryState("MALREC_DEFEATED");
        _pendingRodericReturn = true;
    }

    private void RegisterNpcQuestProgress(NpcQuestType type, string targetId, int amount = 1)
    {
        var questNpcs = _maze.WorldNpcs.Concat(_maze.PartyMembers
            .Where(member => member.TemporaryFollower is not null)
            .Select(member => member.TemporaryFollower!));
        foreach (var npc in questNpcs)
        foreach (var progress in npc.Quests.Where(value => value.State == NpcQuestState.Active).ToArray())
        {
            var quest = _gameData.NpcQuests.FirstOrDefault(value => string.Equals(value.Id, progress.QuestId,
                StringComparison.OrdinalIgnoreCase));
            if (quest?.Type == type && string.Equals(quest.TargetId, targetId, StringComparison.OrdinalIgnoreCase))
            {
                npc.AddQuestProgress(quest.Id, amount, quest.RequiredCount);
                SynchronizeQuestJournal(npc, quest);
            }
        }
    }

    private void ExecuteRestAcknowledgement(AcknowledgeRestCommand command)
    {
        if (_latestRestNotice?.RestId != command.RestId)
        {
            _session.RejectExecutedCommand(command, "Ez a pihenési összegző már nem aktív.");
            return;
        }
        AcknowledgeRest(command.SenderId, command.CharacterId);
    }

    private void AcknowledgeRest(PlayerId playerId, CharacterId characterId)
    {
        if (!_restAcknowledgements.Add(playerId)) return;
        if (_session.ConnectedHumanPlayerIds.Any(other => other != playerId))
            PlaySessionSound(SoundEffect.Waiting, [characterId]);
        var characterName = CharacterRoster.Party.Members
            .FirstOrDefault(character => character.Id == characterId)?.Name ?? "Egy játékos";
        var message = $"✓ {characterName} bezárta a pihenési összegzőt.";
        var otherCharacters = _session.CharacterControls
            .Where(control => control.AssignedPlayerId != playerId && control.AssignedPlayerId is not null)
            .Select(control => control.CharacterId).ToArray();
        RecordSessionActivity(SessionActivityKind.System, message, ConsoleColor.DarkCyan, otherCharacters);
        if (playerId != _session.HostPlayerId) _hostRestAcknowledgementMessages.Add(message);
    }

    private void ExecuteAssignQuickSpell(AssignQuickSpellCommand command)
    {
        var character = CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == command.CharacterId);
        var spell = character?.KnownSpells.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.SpellId, StringComparison.OrdinalIgnoreCase));
        if (character is null || spell is null || !character.AssignQuickSpell(command.QuickSlot, spell))
            _session.RejectExecutedCommand(command, "Csak memorizált varázslat tehető gyorshelyre.");
    }

    private void ExecuteSpellPreparation(PrepareSpellsCommand command)
    {
        if (_activeSpellPreparation is null || _activeSpellPreparation.PromptId != command.PromptId ||
            _activeSpellPreparation.CharacterId != command.CharacterId)
        {
            _session.RejectExecutedCommand(command, "Ez a memorizálási kérés már nem aktív.");
            return;
        }
        var character = CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == command.CharacterId);
        var ids = command.SpellIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var spells = character?.KnownSpells.Where(spell => ids.Contains(spell.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (character is null || spells is null || spells.Length != ids.Length || !character.SetMemorizedSpells(spells))
        {
            _session.RejectExecutedCommand(command, "A választott varázslatlista nem memorizálható.");
            return;
        }
        _spellPreparationCompleted = true;
    }

    private void ExecuteLevelUpPrompt(ResolveLevelUpPromptCommand command)
    {
        if (_activeLevelUpPrompt is null || _activeLevelUpPrompt.PromptId != command.PromptId ||
            _activeLevelUpPrompt.CharacterId != command.CharacterId ||
            (_activeLevelUpPrompt.Kind != LevelUpPromptKind.Summary &&
             _activeLevelUpPrompt.Choices.All(choice => choice.Id != command.ChoiceId)))
        {
            _session.RejectExecutedCommand(command, "Ez a szintlépési választás már nem aktív vagy nem érvényes.");
            return;
        }
        _levelUpResponse = command.ChoiceId;
        _levelUpPromptCompleted = true;
    }

    private void ShowSynchronizedNarrative(NarrativeKind kind, string title, string subtitle,
        IReadOnlyList<string> paragraphs, BossPresentationSnapshot? boss = null)
    {
        var previousPhase = _session.Phase;
        _narrativeAcknowledgements.Clear();
        _activeNarrative = new NarrativeSnapshot(Guid.NewGuid(), kind, title, subtitle, paragraphs, [], boss);
        _session.SetPhase(GameSessionPhase.Paused);
        _renderer.ShowStoryOverlay(title, subtitle, paragraphs, _maze, _fogOfWar, _player.Position, kind, boss);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        while (true)
        {
            ProcessSessionCommands();
            if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
            {
                _narrativeAcknowledgements.Add(_session.HostPlayerId);
                if (_session.ConnectedHumanPlayerIds.Any(player => player != _session.HostPlayerId))
                    PlaySessionSound(SoundEffect.Waiting, [SelectedCharacter.Id]);
            }
            var required = _session.ConnectedHumanPlayerIds;
            if (required.All(_narrativeAcknowledgements.Contains)) break;
            if (_activeCoopHost?.ShouldPublish(DateTime.UtcNow) == true)
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            Thread.Sleep(20);
        }
        _renderer.CloseStoryOverlay();
        _activeNarrative = null;
        _narrativeAcknowledgements.Clear();
        _session.SetPhase(previousPhase);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private void ShowSynchronizedLevelImage(string fileName, string path)
    {
        var previousPhase = _session.Phase;
        _levelImageAcknowledgements.Clear();
        _activeLevelImage = new LevelImageSnapshot(Guid.NewGuid(), _maze.LevelName, fileName, []);
        _session.SetPhase(GameSessionPhase.Paused);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());

        if (!ImageViewer.Show(path))
            _renderer.DrawDeveloperMessage($"Pályakép még nem található: {fileName}");
        AcknowledgeLevelImage(_session.HostPlayerId, SelectedCharacter.Id);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());

        while (true)
        {
            ProcessSessionCommands();
            if (_session.ConnectedHumanPlayerIds.All(_levelImageAcknowledgements.Contains)) break;
            if (_activeCoopHost?.ShouldPublish(DateTime.UtcNow) == true)
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            Thread.Sleep(20);
        }

        _activeLevelImage = null;
        _levelImageAcknowledgements.Clear();
        _session.SetPhase(previousPhase);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private void AcknowledgeLevelImage(PlayerId playerId, CharacterId characterId)
    {
        if (!_levelImageAcknowledgements.Add(playerId)) return;
        var characterName = CharacterRoster.Party.Members
            .FirstOrDefault(character => character.Id == characterId)?.Name ?? "Egy játékos";
        var message = $"👤 {characterName} készen áll a játékra.";
        var otherCharacters = _session.CharacterControls
            .Where(control => control.AssignedPlayerId != playerId && control.AssignedPlayerId is not null &&
                              control.ConnectionState == PlayerConnectionState.Connected)
            .Select(control => control.CharacterId).ToArray();
        RecordSessionActivity(SessionActivityKind.System, message, ConsoleColor.Green, otherCharacters);
        if (playerId != _session.HostPlayerId)
            _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
    }

    private void ShowSynchronizedRest(PartyRestSnapshot rest)
    {
        var previousPhase = _session.Phase;
        _restAcknowledgements.Clear();
        _hostRestAcknowledgementMessages.Clear();
        _latestRestNotice = rest;
        _session.SetPhase(GameSessionPhase.Paused);
        DrawRestSummaryForHost();
        var renderedAcknowledgementCount = _restAcknowledgements.Count;
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        while (true)
        {
            ProcessSessionCommands();
            if (Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                AcknowledgeRest(_session.HostPlayerId, SelectedCharacter.Id);
            var required = _session.ConnectedHumanPlayerIds;
            if (required.All(_restAcknowledgements.Contains)) break;
            if (_restAcknowledgements.Count != renderedAcknowledgementCount)
            {
                DrawRestSummaryForHost();
                renderedAcknowledgementCount = _restAcknowledgements.Count;
            }
            if (_activeCoopHost?.ShouldPublish(DateTime.UtcNow) == true)
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            Thread.Sleep(20);
        }
        _latestRestNotice = null;
        _restAcknowledgements.Clear();
        _session.SetPhase(previousPhase);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        foreach (var message in _hostRestAcknowledgementMessages)
            _renderer.DrawInventoryMessage(message, ConsoleColor.DarkCyan);
        _hostRestAcknowledgementMessages.Clear();
    }

    private void DrawRestSummaryForHost()
    {
        if (_latestRestNotice is not { } rest) return;
        var acknowledged = _restAcknowledgements.Contains(_session.HostPlayerId);
        _renderer.DrawRestSummaryScreen(rest,
            acknowledged ? "❖  Várakozás a másik játékosra…  ❖" : "❖  Nyomj Entert a folytatáshoz...  ❖",
            acknowledged ? ConsoleColor.DarkCyan : ConsoleColor.Green);
    }

    private void ContinueDisconnectedRemoteBattleAsNpc()
    {
        if (_activeTeamBattle is { IsCompleted: false } teamBattle &&
            teamBattle.CurrentCharacter is { } teamCharacter &&
            !_session.IsHumanControlled(teamCharacter.Id))
        {
            ContinueTeamBattle();
            return;
        }
        return;
    }

    private void ExecuteLeaderAction(LeaderAction action)
    {
        switch (action)
        {
            case LeaderAction.ToggleFormation:
                ToggleFormation();
                break;
            case LeaderAction.RotateFormationLeft:
                RotateFormation(clockwise: false);
                break;
            case LeaderAction.RotateFormationRight:
                RotateFormation(clockwise: true);
                break;
            case LeaderAction.ToggleRegrouping:
                TogglePartyRegrouping();
                break;
            case LeaderAction.ToggleHoldPosition:
                TogglePartyHoldPosition();
                break;
            case LeaderAction.ScatterParty:
                ScatterPartyTemporarily();
                break;
            case LeaderAction.ToggleAttackMode:
                TogglePartyAttackMode();
                break;
            case LeaderAction.Rest:
                TryRestParty();
                break;
            case LeaderAction.ActivateExit:
                ActivateExit();
                break;
        }
    }

    private void EditFormation()
    {
        NormalizeFormation();
        var slots = FormationEditor.Edit(CharacterRoster.Party.Members.Where(member => member.IsAlive).ToArray(),
            _formation);
        _formation = PartyFormationRules.WithSlots(_formation, slots);
        _renderer.SetFormationStatus(_formation);
        _session.SetFormationMovementLocked(false);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
        _renderer.SetCharacterSheetFocused(true);
        AnnouncePartyCommand("Az alakzat sorrendje elmentve. A terkepen A-val rendelheted el az osszeallast.",
            ConsoleColor.Cyan);
    }

    private void NormalizeFormation()
    {
        _formation = PartyFormationController.Normalize(_formation,
            CharacterRoster.Party.Members.Where(member => member.IsAlive).Select(member => member.Id),
            SelectedCharacter.Id, out var transitionedToAssembling);
        if (transitionedToAssembling)
        {
            _session.SetFormationMovementLocked(false);
            _formationObstacleReported = false;
        }
        _renderer.SetFormationStatus(_formation);
    }

    private void ToggleFormation()
    {
        NormalizeFormation();
        if (_formation.State != PartyFormationState.Disbanded)
        {
            _formation = PartyFormationRules.WithState(_formation, PartyFormationState.Disbanded);
            _renderer.SetFormationStatus(_formation);
            _session.SetFormationMovementLocked(false);
            _formationObstacleReported = false;
            AnnouncePartyCommand("Az alakzat feloszlott; minden partitag ujra egyenileg mozoghat.", ConsoleColor.Gray);
            return;
        }
        _formation = _formation with { Facing = _leaderFacing, State = PartyFormationState.Assembling };
        _renderer.SetFormationStatus(_formation);
        _partyHoldingPosition = false;
        _partyRegrouping = false;
        _partyAttackMode = false;
        _partyScatterUntil = null;
        _formationObstacleReported = false;
        foreach (var member in _maze.PartyMembers) _nextPartyMoves[member] = DateTime.UtcNow;
        AnnouncePartyCommand("ALAKZAT: a partitagok elfoglaljak a beallitott 2x2-es helyuket.", ConsoleColor.Cyan);
    }

    private void RotateFormation(bool clockwise)
    {
        if (_formation.State != PartyFormationState.Locked)
        {
            _renderer.DrawDeveloperMessage("Fordulni csak teljesen osszeallt alakzattal lehet.");
            return;
        }
        if (!CanControlledCharacterMove(SelectedCharacter)) return;
        var rotated = PartyFormationController.Rotate(_formation, clockwise);
        var positions = PartyFormationController.Positions(rotated, SelectedCharacter.Id, _player.Position);
        if (!CanFormationOccupy(positions))
        {
            _renderer.DrawDeveloperMessage("Az alakzat itt nem tud 90 fokot fordulni: legalabb egy celmezo foglalt.");
            return;
        }
        ApplyFormationPositions(positions, rotated.Facing);
        ScheduleFormationMove();
        AnnouncePartyCommand(clockwise ? "Az alakzat jobbra fordult." : "Az alakzat balra fordult.",
            ConsoleColor.Cyan);
    }

    private void MoveLockedFormation(Direction direction)
    {
        var current = PartyFormationController.Positions(_formation, SelectedCharacter.Id, _player.Position);
        var destinations = current.ToDictionary(pair => pair.Key, pair => pair.Value + direction);
        var enemyEntry = destinations.Select(pair => (pair.Key, Enemy: _maze.GetEnemyAt(pair.Value)))
            .FirstOrDefault(entry => entry.Enemy is not null);
        if (enemyEntry.Enemy is { } enemy)
        {
            var avatar = FormationAvatar(enemyEntry.Key);
            if (avatar is null) StartBattle(enemy);
            else StartBattle(avatar, enemy);
            return;
        }
        if (!CanFormationOccupy(destinations)) return;
        ApplyFormationPositions(destinations, _formation.Facing);
        _leaderFacing = direction;
        if (_leaderTrail[^1] != _player.Position) _leaderTrail.Add(_player.Position);
        if (_leaderTrail.Count > 256) _leaderTrail.RemoveRange(0, _leaderTrail.Count - 256);
        ScheduleFormationMove();
    }

    private bool CanFormationOccupy(IReadOnlyDictionary<CharacterId, Position> positions) =>
        PartyFormationController.CanFormationOccupy(positions, _maze, FormationAvatar);

    private void ApplyFormationPositions(IReadOnlyDictionary<CharacterId, Position> positions, Direction facing)
    {
        var previousLeader = _player.Position;
        var previousMembers = positions.Keys.Where(id => id != SelectedCharacter.Id)
            .Select(id => (Avatar: FormationAvatar(id), Destination: positions[id]))
            .Where(entry => entry.Avatar is not null)
            .Select(entry => (Avatar: entry.Avatar!, Previous: entry.Avatar!.Position, entry.Destination)).ToArray();
        _player.TeleportTo(positions[SelectedCharacter.Id]);
        foreach (var entry in previousMembers) entry.Avatar.MoveTo(entry.Destination);
        _formation = _formation with { Facing = facing };
        _renderer.SetFormationStatus(_formation);

        var revealed = RevealFor(SelectedCharacter, _player.Position, advanceEnemyMemory: true);
        _renderer.DrawMovement(_maze, _fogOfWar, previousLeader, _player.Position, revealed,
            _player.Position == _maze.Exit && previousLeader != _maze.Exit);
        SelectedCharacter.RegisterExplorationStep();
        PlayCharacterStepSound(SelectedCharacter);
        CollectTreasureChest(SelectedCharacter, _player.Position, shareLootWithParty: true);
        TriggerTrapAt(SelectedCharacter, _player.Position);
        foreach (var entry in previousMembers)
        {
            entry.Avatar.Character.RegisterExplorationStep();
            var memberRevealed = RevealFor(entry.Avatar.Character, entry.Destination, advanceEnemyMemory: true);
            _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, entry.Previous, entry.Destination, memberRevealed,
                _player.Position);
            CollectTreasureChest(entry.Avatar.Character, entry.Destination, shareLootWithParty: false);
            TriggerTrapAt(entry.Avatar.Character, entry.Destination);
        }
        CheckBossDiscoveryAt(revealed, SelectedCharacter);
    }

    private void ScheduleFormationMove()
    {
        var delay = PartyFormationController.CalculateMoveDelay(CharacterRoster.Party.Members,
            ControlledMoveDelayMilliseconds);
        var next = DateTime.UtcNow + TimeSpan.FromMilliseconds(delay);
        foreach (var member in CharacterRoster.Party.Members) _nextControlledMoves[member.Id] = next;
    }

    private PartyMemberAvatar? FormationAvatar(CharacterId id) =>
        _maze.PartyMembers.FirstOrDefault(member => !member.IsTemporaryFollower && member.Character.Id == id);

    private void ExecuteCharacterAction(CharacterActionCommand command)
    {
        var character = CharacterRoster.Party.Members.FirstOrDefault(candidate => candidate.Id == command.CharacterId);
        var position = character is null ? null : GetCharacterWorldPosition(character);
        if (character is null || position is null || !character.IsAlive) return;
        var isLeader = character == SelectedCharacter;
        var doorContext = ResolveDoorInteraction(character, position.Value, command.Action,
            command.TargetDoorPosition);
        var keyOwners = DoorKeyOwners(character);
        switch (command.Action)
        {
            case CharacterAction.OpenDoor:
                _doorInteractions.TryOpenAdjacentDoor(_maze, _fogOfWar, doorContext.Origin, _player.Position,
                    character, allowPartyAssistanceAndPrompts: isLeader, doorContext.Target, command.UseKey,
                    command.KeyOwnerCharacterId, keyOwners);
                break;
            case CharacterAction.CloseOrLockDoor:
                _doorInteractions.TryCloseOrLockAdjacentDoor(_maze, _fogOfWar, doorContext.Origin, _player.Position,
                    character, allowPartyAssistanceAndPrompts: isLeader, doorContext.Target, command.UseKey,
                    command.KeyOwnerCharacterId, keyOwners);
                break;
            case CharacterAction.SearchCurrentPosition:
                if (!TryDisarmAdjacentTrap(character, position.Value))
                    TrySearchCurrentCell(character, position.Value, shareLootWithParty: isLeader);
                break;
        }
    }

    private IReadOnlyList<Position> AdjacentDoorPositions(LiveCharacter character, Position position,
        bool includeFormation) =>
        DoorInteractionOrigins(character, position, includeFormation)
            .SelectMany(origin => Enum.GetValues<Direction>().Select(direction => origin + direction))
            .Where(candidate => _maze.GetDoorAt(candidate) is not null)
            .Distinct()
            .ToArray();

    private IReadOnlyList<Position> DoorInteractionOrigins(LiveCharacter character, Position position,
        bool includeFormation)
    {
        if (!includeFormation) return [position];
        var partyPositions = CharacterRoster.Party.Members.Where(member => member.IsAlive)
            .Select(member => (member.Id, Position: GetCharacterWorldPosition(member)))
            .Where(entry => entry.Position is not null)
            .ToDictionary(entry => entry.Id, entry => entry.Position!.Value);
        return PartyFormationRules.InteractionOrigins(_formation, character.Id, position, partyPositions);
    }

    private IReadOnlyList<LiveCharacter> DoorKeyOwners(LiveCharacter character)
    {
        if (_formation.State != PartyFormationState.Locked || !_formation.Slots.Contains(character.Id))
            return [character];
        return _formation.Slots.Where(id => id is not null)
            .Select(id => CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == id!.Value))
            .Where(member => member is { IsAlive: true })
            .Cast<LiveCharacter>()
            .ToArray();
    }

    private (Position Origin, Position? Target) ResolveDoorInteraction(LiveCharacter character,
        Position actorPosition, CharacterAction action, Position? requestedTarget)
    {
        var includeFormation = action is CharacterAction.OpenDoor or CharacterAction.CloseOrLockDoor;
        var candidates = AdjacentDoorPositions(character, actorPosition, includeFormation);
        var target = requestedTarget ?? (candidates.Count == 1 ? candidates[0] : null);
        if (target is not { } targetPosition || !candidates.Contains(targetPosition))
            return (actorPosition, requestedTarget);
        var origin = DoorInteractionOrigins(character, actorPosition, includeFormation)
            .First(position => Manhattan(position, targetPosition) == 1);
        return (origin, targetPosition);
    }

    private (bool? UseKey, CharacterId? KeyOwnerCharacterId) GetLocalThiefKeyChoice(
        CharacterAction action, Position? targetDoorPosition)
    {
        var keyOwner = DoorKeyOwners(SelectedCharacter).FirstOrDefault(DoorInteractionRules.HasKey);
        if (!CharacterClassRules.IsThief(SelectedCharacter.CharacterClass.Id) ||
            keyOwner is null ||
            targetDoorPosition is not { } target || _maze.GetDoorAt(target) is not { } door ||
            action switch
            {
                CharacterAction.OpenDoor => door.State != DoorState.Locked,
                CharacterAction.CloseOrLockDoor => door.State != DoorState.Closed,
                _ => true
            }) return (null, null);

        _renderer.DrawDoorMessage(
            $"🔑 {keyOwner.Name} kulcsát használjuk? I/Y/Enter: igen | N/Esc: nem, jöjjön a tolvajpróba",
            ConsoleColor.Yellow);
        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.I or ConsoleKey.Y or ConsoleKey.Enter) return (true, keyOwner.Id);
            if (key is ConsoleKey.N or ConsoleKey.Escape) return (false, null);
        }
    }

    private Position? SelectDoorTarget(IReadOnlyList<Position> doors, CharacterAction action)
    {
        var selected = 0;
        Position? previous = null;
        while (true)
        {
            var current = doors[selected];
            var verb = action == CharacterAction.OpenDoor ? "nyitás" : "bezárás/zárás";
            _renderer.DrawSpellTargetCursor(_maze, _fogOfWar, previous, current, true,
                $"Ajtó kiválasztása ({verb}): nyilak/Tab, Enter: kész, Esc: mégse");
            previous = current;
            _activeCoopHost?.TryPublish(CreateSessionSnapshot());
            while (!Console.KeyAvailable)
            {
                ProcessSessionCommands();
                if (_activeCoopHost?.ShouldPublish(DateTime.UtcNow) == true)
                    _activeCoopHost.TryPublish(CreateSessionSnapshot());
                Thread.Sleep(20);
            }
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Escape)
            {
                _renderer.FinishSpellTargeting(_maze, _fogOfWar, _player.Position);
                return null;
            }
            if (key == ConsoleKey.Enter)
            {
                _renderer.FinishSpellTargeting(_maze, _fogOfWar, _player.Position);
                return current;
            }
            if (key == ConsoleKey.Tab)
                selected = (selected + 1) % doors.Count;
            else if (TryGetDirection(key, out var direction))
            {
                var directionalDoor = _player.Position + direction;
                var index = doors.ToList().IndexOf(directionalDoor);
                if (index >= 0) selected = index;
            }
        }
    }

    private void ActivateExit()
    {
        if (_player.Position != _maze.Exit) return;
        if (_locationKind == AdventureLocationKind.Quest)
        {
            _renderer.DrawInventoryMessage(
                "A sírkápolnából csak Roderic vezethet vissza. Előbb győzzétek le Sir Malrecet.",
                ConsoleColor.DarkYellow);
            return;
        }
        if (_isReturnExpedition)
        {
            ReturnFromExpeditionToInn();
            return;
        }
        if (_maze.PartyMembers.FirstOrDefault(member => member.IsTemporaryFollower) is { } escort &&
            Manhattan(escort.Position, _player.Position) > 3)
        {
            _renderer.DrawInventoryMessage($"🌿 {escort.Character.Name} túl messze van a kijárattól. Várjátok meg vagy hívjátok magatokhoz Gyülekező paranccsal.",
                ConsoleColor.Yellow);
            return;
        }
        ResolveTemporaryFollowerAtExit();
        if (_mazeLevel == MazeLevelConfigurations.FinalLevel)
        {
            CompleteCampaign();
            return;
        }
        var completedLevel = _mazeLevel;
        PlaySessionSound(SoundEffect.LevelComplete);
        _backgroundMusic.EnterInn();
        _session.SetPhase(GameSessionPhase.Inn);
        var expeditionReason = ReturnExpeditionReason(completedLevel);
        var departure = _innController.Run(completedLevel, expeditionReason);
        if (departure == InnController.DepartureChoice.ReturnExpedition)
        {
            BeginReturnExpedition();
            return;
        }
        _activeInnDeparture = new InnDepartureSnapshot("A csapat szedelőzködik, és elhagyjátok a fogadót.");
        _session.SetPhase(GameSessionPhase.Paused);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        CarryPersistentTemporaryFollowers();
        _mazeLevel++;
        StartNewMaze();
    }

    private string? ReturnExpeditionReason(int completedLevel)
    {
        var activeQuestIds = _questJournal.Values.Where(entry => entry.Status == QuestJournalStatus.Active)
            .Select(entry => entry.QuestId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var quest = _gameData.NpcQuests.FirstOrDefault(candidate => activeQuestIds.Contains(candidate.Id) &&
            _gameData.NpcEncounters.Any(encounter => encounter.MazeLevel == completedLevel &&
                string.Equals(encounter.NpcId, candidate.NpcId, StringComparison.OrdinalIgnoreCase)));
        if (quest is not null)
            return $"Aktív küldetés maradt hátra: {quest.Title}. " + Environment.NewLine + " A régi kijárat visszahoz ugyanebbe a fogadóba.";
        return _random.Next(100) < 35
            ? "Egy fogadói szóbeszéd új nyomot jelzett az előző pályán. " + Environment.NewLine + "A régi kijárat visszahoz ugyanebbe a fogadóba."
            : null;
    }

    private void BeginReturnExpedition()
    {
        _isReturnExpedition = true;
        _session.SetPhase(GameSessionPhase.Exploration);
        _session.SynchronizeParty();
        foreach (var boss in _maze.Enemies.Where(enemy => enemy.Definition.IsBoss).ToArray())
        {
            _maze.RemoveEnemy(boss);
            _nextEnemyMoves.Remove(boss);
        }
        ReplenishExpeditionEnemies();
        RepositionPartyAtEntrance();
        foreach (var character in CharacterRoster.Party.Members.Where(character => character.IsAlive))
        {
            character.ConsumeFood(ReturnExpeditionRules.TravelNeedCost);
            character.ConsumeWater(ReturnExpeditionRules.TravelNeedCost);
            character.SynchronizeNeedStatuses(_gameData.GetStatus(CharacterStatusIds.Hungry),
                _gameData.GetStatus(CharacterStatusIds.Thirsty));
        }
        _spottedEnemyIds.Clear();
        _spottedChestIds.Clear();
        _battleStarted = false;
        _hasRestedThisLevel = true;
        InitializeEnemyMoveSchedule(DateTime.UtcNow);
        RevealFor(SelectedCharacter, _player.Position);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
        var message = "🗺️ Visszatérő expedíció: a felderített térkép megmaradt, a vidéket csak kisebb szörnyjárőrök népesítették be újra. 🍖-3 💧-3";
        _renderer.DrawInventoryMessage(message, ConsoleColor.Cyan);
        RecordSessionActivity(SessionActivityKind.System, message, ConsoleColor.Cyan);
        _backgroundMusic.SynchronizeMazeLevel(_mazeLevel, _fogOfWar.IsRevealed(_maze.Exit));
        _activeInnDeparture = null;
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private void ReturnFromExpeditionToInn()
    {
        _isReturnExpedition = false;
        PlaySessionSound(SoundEffect.LevelComplete);
        _backgroundMusic.EnterInn();
        _session.SetPhase(GameSessionPhase.Inn);
        _innController.Run(_mazeLevel, resume: true);
        _activeInnDeparture = new InnDepartureSnapshot("A csapat szedelőzködik, és elhagyjátok a fogadót.");
        _session.SetPhase(GameSessionPhase.Paused);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        CarryPersistentTemporaryFollowers();
        _mazeLevel++;
        StartNewMaze();
    }

    private void CaptureExpeditionEnemyTemplates() =>
        DungeonExpeditionCoordinator.CaptureExpeditionEnemyTemplates(_levelEnemyTemplates, _maze, _gameData);

    private void ReplenishExpeditionEnemies() =>
        _expeditionCoordinator.ReplenishExpeditionEnemies(_levelEnemyTemplates, _maze);

    private Position? FindExpeditionSpawnPosition(Position preferred) =>
        DungeonExpeditionCoordinator.FindExpeditionSpawnPosition(_maze, preferred);

    private void RepositionPartyAtEntrance()
    {
        var oldAvatars = _maze.PartyMembers.Where(member => member.Character.IsAlive)
            .Select(member => (member.Character, member.TemporaryFollower)).ToList();
        foreach (var member in _maze.PartyMembers.ToArray()) _maze.RemovePartyMember(member);
        _player.TeleportTo(_maze.Entrance);
        _leaderTrail.Clear();
        _leaderTrail.Add(_player.Position);
        _nextPartyMoves.Clear();
        foreach (var character in CharacterRoster.Party.Members.Where(character => character != SelectedCharacter &&
                     character.IsAlive && oldAvatars.All(entry => entry.Character != character)))
            oldAvatars.Add((character, null));
        var positions = FindNearbyFreePositions(_player.Position).Take(oldAvatars.Count).ToArray();
        for (var index = 0; index < Math.Min(oldAvatars.Count, positions.Length); index++)
        {
            var avatar = new PartyMemberAvatar(positions[index], oldAvatars[index].Character,
                oldAvatars[index].TemporaryFollower);
            _maze.AddPartyMember(avatar);
            _nextPartyMoves[avatar] = DateTime.UtcNow;
        }
    }

    private void ResolveTemporaryFollowerAtExit()
    {
        var avatar = _maze.PartyMembers.FirstOrDefault(member => member.TemporaryFollower is not null);
        if (avatar?.TemporaryFollower is not { } follower) return;
        if (string.Equals(follower.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase)) return;
        foreach (var progress in follower.Quests.Where(value => value.State == NpcQuestState.Active).ToArray())
        {
            var quest = _gameData.NpcQuests.FirstOrDefault(value =>
                string.Equals(value.Id, progress.QuestId, StringComparison.OrdinalIgnoreCase));
            if (quest is { Type: NpcQuestType.Escort })
            {
                follower.AddQuestProgress(quest.Id, 1, quest.RequiredCount);
                SynchronizeQuestJournal(follower, quest);
            }
        }
        ProcessNpcQuests(follower);
        follower.AdjustFriendliness(2);

        var joined = false;
        if (follower.Friendliness >= 10)
        {
            var hasRoom = CharacterRoster.Party.Members.Count < Party.MaximumSize;
            joined = _renderer.ConfirmUniqueNpcPermanentJoin(follower, hasRoom) &&
                     CharacterRoster.Party.Add(follower.Character);
        }
        if (joined)
        {
            follower.Character.SetNpcJoinOrigin(_mazeLevel, "A pálya kijáratánál csatlakozott");
            avatar.MakePermanent();
            _renderer.DrawInventoryMessage($"🤝 {follower.Character.Name} végleg csatlakozott a partihoz.", ConsoleColor.Green);
            return;
        }

        _maze.RemovePartyMember(avatar);
        _nextPartyMoves.Remove(avatar);
        CharacterRoster.Remove(follower.Character);
        _renderer.DrawInventoryMessage(follower.Friendliness >= 10
            ? $"🌿 {follower.Character.Name} hálásan elbúcsúzott."
            : $"🌿 {follower.Character.Name} kijutott de még nem bízik eléggé a végleges csatlakozáshoz ({follower.Friendliness}/10).",
            ConsoleColor.Cyan);
    }

    private void DropSelectedInventoryItem()
    {
        var slot = _renderer.GetSelectedInventorySlot();
        if (slot is null) { _renderer.DrawInventoryMessage("Itt nincs ledobható tárgy.", ConsoleColor.DarkYellow); return; }
        var item = slot.Value.Character.GetInventoryItem(slot.Value.Kind, slot.Value.Index);
        if (item is null) { _renderer.DrawInventoryMessage("A kijelölt hely üres.", ConsoleColor.DarkYellow); return; }
        if (SpellcastingRules.IsSpellcastingFocus(item))
        { _renderer.DrawInventoryMessage($"A(z) {item.Name} a karakterhez kötött varázsfókusz, ezért nem dobható el.", ConsoleColor.Red); return; }
        if (CharacterBoundItemRules.IsBound(item))
        { _renderer.DrawInventoryMessage($"A(z) {item.Name} családi ereklye, ezért nem dobható el.", ConsoleColor.Red); return; }
        var commandId = _localCommandId + 1;
        if (!_session.Submit(new DropInventoryItemCommand(_session.HostPlayerId, commandId,
                slot.Value.Character.Id, slot.Value.Character.InventoryRevision, slot.Value.Kind, slot.Value.Index))) return;
        _localCommandId = commandId;
    }

    private bool TrySearchCurrentCell(LiveCharacter character, Position position, bool shareLootWithParty)
    {
        var corpses = _maze.GetCorpsesAt(position);
        var pile = _maze.GetGroundItemPileAt(position);
        if (corpses.Count == 0 && pile is null) return false;
        var unsearched = _maze.GetUnsearchedMonsterCorpsesAt(position);
        if (unsearched.Count == 0 && pile is null && corpses.All(corpse => corpse is MonsterCorpse)) return false;

        var messages = new List<string>();
        foreach (var monsterCorpse in unsearched)
        {
            monsterCorpse.MarkSearched();
            var corpseMessages = new List<string>();
            SearchMonsterCorpse(monsterCorpse, character, position, shareLootWithParty, corpseMessages);
            messages.Add($"† {monsterCorpse.FormerName}: {string.Join(", ", corpseMessages)}");
        }
        if (unsearched.Count == 0 && corpses.Any(corpse => corpse is PartyMemberCorpse))
            messages.Add("Az elesett társ testén nincs elvehető zsákmány");
        else if (unsearched.Count == 0 && corpses.Any(corpse => corpse is not MonsterCorpse))
            messages.Add("Ez a régi tetem már nem tartalmaz azonosítható zsákmányt");

        PickUpGroundItems(character, position, shareLootWithParty, messages);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        string[] resultMessages = messages.Count == 0
            ? ["🔎 A keresés nem hozott eredményt."]
            : messages.Select(message => $"🔎 {message}.").ToArray();
        foreach (var resultMessage in resultMessages)
        {
            _renderer.DrawInventoryMessage(resultMessage, ConsoleColor.Yellow);
            RecordSessionActivity(SessionActivityKind.System, resultMessage, ConsoleColor.Yellow, [character.Id]);
        }
        return true;
    }

    private void SearchMonsterCorpse(MonsterCorpse corpse, LiveCharacter character, Position position,
        bool shareLootWithParty, ICollection<string> messages)
    {
        var enemy = _gameData.GetEnemy(corpse.EnemyDefinitionId);
        var rules = _gameData.LootRules;
        var keyChance = _isReturnExpedition ? 0 : AdjustedSearchChance(character, rules.KeyChancePercent);
        var goldChance = AdjustedSearchChance(character, rules.GoldChancePercent);
        var equipmentDefinition = _gameData.GetMonsterLoot(enemy.Id);
        var equipmentChance = equipmentDefinition is null
            ? 0
            : AdjustedSearchChance(character, equipmentDefinition.EquipmentChancePercent);
        var carriedWeaponChance = corpse.CarriedWeaponIds.Count == 0
            ? 0
            : AdjustedSearchChance(character, rules.CarriedWeaponChancePercent);
        messages.Add($"esélyek: 🔑 {keyChance}%, {ConsoleRenderer.MoneyIcon} {goldChance}%" +
                     (carriedWeaponChance == 0 ? string.Empty : $", ⚔ saját fegyver {carriedWeaponChance}%") +
                     (equipmentDefinition is null ? string.Empty : $", 🎁 {equipmentChance}%"));

        var foundItems = corpse.GuaranteedLootIds.Select(_gameData.GetItem).Cast<IItemDefinition>().ToList();
        if (_random.Next(100) < keyChance) foundItems.Add(_gameData.GetItem(MiscItemIds.Key));
        if (_random.Next(100) < goldChance)
        {
            var maximumGold = Math.Max(1, enemy.StrengthTier * rules.GoldPerStrengthTier);
            var gold = _random.Next(1, maximumGold + 1);
            SelectedCharacter.AddGold(gold);
            messages.Add($"{ConsoleRenderer.MoneyIcon} {gold} arany");
        }
        if (_lootService.RollCarriedWeapon(corpse.CarriedWeaponIds, carriedWeaponChance) is { } carriedWeapon)
            foundItems.Add(carriedWeapon);
        else if (equipmentDefinition is not null && _random.Next(100) < equipmentChance &&
                 RollEquipmentLoot(equipmentDefinition) is { } equipment)
            foundItems.Add(equipment);

        foreach (var item in foundItems)
        {
            if (TryStoreSearchedLoot(character, item, shareLootWithParty, out var owner))
                messages.Add($"{item.Name} → {owner} hátizsákja");
            else
            {
                _maze.DropItem(position, item);
                messages.Add($"{item.Name} a földön maradt (a hátizsákok tele vannak)");
            }
        }
        if (foundItems.Count == 0 && messages.All(message => !message.StartsWith(ConsoleRenderer.MoneyIcon, StringComparison.Ordinal)))
            messages.Add("a tetemnél nem találtál zsákmányt");
    }

    private int AdjustedSearchChance(LiveCharacter character, int baseChance) =>
        _lootService.AdjustedSearchChance(character, baseChance);

    private IItemDefinition? RollEquipmentLoot(MonsterLootDefinition loot) =>
        _lootService.RollEquipmentLoot(loot);

    private IItemDefinition? RollMasterThiefChestLoot(LiveCharacter character) =>
        _lootService.RollMasterThiefChestLoot(character, AllTradableItems());

    private bool TryStoreLootInParty(IItemDefinition item, out string ownerName) =>
        LootAndInventoryService.TryStoreLootInParty(item, SelectedCharacter, CharacterRoster.Party.Members, out ownerName);

    #region Inventory & Loot

    private bool TryStoreSearchedLoot(LiveCharacter character, IItemDefinition item, bool shareLootWithParty,
        out string ownerName) =>
        LootAndInventoryService.TryStoreSearchedLoot(character, item, shareLootWithParty, CharacterRoster.Party.Members, out ownerName);

    private void PickUpGroundItems(LiveCharacter character, Position position, bool shareLootWithParty,
        ICollection<string> messages)
    {
        var pile = _maze.GetGroundItemPileAt(position);
        if (pile is null) return;
        var pickedUp = new List<string>();
        foreach (var item in pile.Items.ToArray())
        {
            if (!TryStoreSearchedLoot(character, item, shareLootWithParty, out var owner)) continue;
            pile.Remove(item);
            pickedUp.Add($"{item.Name} → {owner}");
        }
        if (pickedUp.Count > 0) messages.Add("felvéve: " + string.Join(", ", pickedUp));
        if (pile.Items.Count == 0) _maze.RemoveGroundItemPile(pile);
        else messages.Add($"a földön maradt {pile.Items.Count} tárgy (nincs hely)");
    }

    private void InspectSelectedInventoryItem()
    {
        var slot = _renderer.GetSelectedInventorySlot();
        if (slot is null && _renderer.GetSelectedPartyMember() is { } partyMember)
        {
            _renderer.DrawInventoryMessage($"{partyMember.Name} — mozgásprofil: {NpcBehaviorName(partyMember.NpcBehavior)}.", partyMember.Color);
            return;
        }
        var item = slot is { } selected ? selected.Character.GetInventoryItem(selected.Kind, selected.Index) : null;
        if (item is null) { _renderer.DrawInventoryMessage("A kijelölt helyen nincs megvizsgálható tárgy.", ConsoleColor.DarkYellow); return; }

        var inspection = ItemInspectionFormatter.Format(item, _gameData,
            slot is { } itemSlot
                ? itemSlot.Character.GetInventoryItemCharges(itemSlot.Kind, itemSlot.Index)
                : 0,
            slot?.Character.WeaponProficiencies.ToDictionary(proficiency => proficiency.FamilyId,
                proficiency => (int)proficiency.Rank, StringComparer.OrdinalIgnoreCase),
            slot is { } previewSlot
                ? new ItemInspectionMobilityContext(CreateCharacterDetailsSnapshot(previewSlot.Character),
                    previewSlot.Kind, previewSlot.Index)
                : null);
        _renderer.DrawInventoryMessage(inspection.Text, inspection.Color);
    }

    private void DismissSelectedPartyMember()
    {
        var character = _renderer.GetSelectedPartyMember();
        if (character is null)
        {
            _renderer.DrawInventoryMessage("A Del használatához jelölj ki egy partitársat.", ConsoleColor.DarkYellow);
            return;
        }
        if (character == SelectedCharacter)
        {
            _renderer.DrawInventoryMessage("👑 A party leaderét nem lehet kirúgni.", ConsoleColor.DarkYellow);
            return;
        }

        CancelHeldInventoryItem();
        _renderer.DrawInventoryMessage(
            $"⚠️ Biztosan kirúgod {character.Name} karaktert? Felszerelésével együtt végleg távozik. I/Y: igen | N/Esc: nem",
            ConsoleColor.Red);
        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.N or ConsoleKey.Escape)
            {
                _renderer.DrawInventoryMessage($"{character.Name} a partiban marad.", ConsoleColor.DarkYellow);
                return;
            }
            if (key is not (ConsoleKey.I or ConsoleKey.Y)) continue;
            break;
        }

        var changedPositions = new List<Position>();
        var avatar = _maze.PartyMembers.FirstOrDefault(member => member.Character == character);
        if (avatar is not null)
        {
            changedPositions.Add(avatar.Position);
            _maze.RemovePartyMember(avatar);
            _nextPartyMoves.Remove(avatar);
        }
        foreach (var corpse in _maze.Corpses.OfType<PartyMemberCorpse>()
                     .Where(corpse => corpse.Character == character).ToList())
        {
            changedPositions.Add(corpse.Position);
            _maze.RemoveCorpse(corpse);
        }

        CharacterRoster.Remove(character);
        foreach (var position in changedPositions.Distinct())
            _renderer.DrawMapCellAfterBattle(_maze, _fogOfWar, position, _player.Position);
        _renderer.RefreshAfterPartyMemberRemoved(character, SelectedCharacter);
        _renderer.DrawInventoryMessage($"👋 {character.Name} felszerelésével együtt végleg távozott a partiból.",
            ConsoleColor.DarkYellow);
        TryFinalizeRodericPermanentJoin();
    }

    private bool ConfirmReturnToMainMenu()
    {
        _renderer.DrawInventoryMessage(
            "⚠️ Visszatérsz a főmenübe? A legutóbbi mentés óta történt változások elvesznek. I/Y: igen | N/Esc: maradok",
            ConsoleColor.Red);
        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.I or ConsoleKey.Y) return true;
            if (key is ConsoleKey.N or ConsoleKey.Escape)
            {
                _renderer.DrawInventoryMessage("A játék folytatódik.", ConsoleColor.Cyan);
                return false;
            }
        }
    }

    private void UseSelectedInventoryItem()
    {
        var slot = _renderer.GetSelectedInventorySlot();
        if (slot is { } reserve && reserve.Kind == InventorySlotKind.Weapon && reserve.Index == 2)
        {
            var character = reserve.Character;
            var swapCommandId = _localCommandId + 1;
            if (_session.Submit(new InventoryTransferCommand(_session.HostPlayerId, swapCommandId, character.Id,
                character.InventoryRevision, InventorySlotKind.Weapon, 2, character.Id,
                character.InventoryRevision, InventorySlotKind.Weapon, 0))) _localCommandId = swapCommandId;
            return;
        }
        if (slot is null || slot.Value.Kind != InventorySlotKind.Backpack)
        { _renderer.DrawInventoryMessage("Használható tárgyat a hátizsákban jelölj ki.", ConsoleColor.DarkYellow); return; }
        var selectedItem = slot.Value.Character.GetInventoryItem(slot.Value.Kind, slot.Value.Index);
        if (SpellcastingRules.IsSpellcastingFocus(selectedItem))
        {
            _renderer.DrawSpellInfoPage(slot.Value.Character, 0);
            return;
        }
        if (selectedItem is not MiscItemDefinition item || item.Effect == ConsumableEffect.None)
        { _renderer.DrawInventoryMessage("A kijelölt tárgy közvetlenül nem használható.", ConsoleColor.DarkYellow); return; }

        var commandId = _localCommandId + 1;
        if (!_session.Submit(new UseInventoryItemCommand(_session.HostPlayerId, commandId,
                slot.Value.Character.Id, slot.Value.Character.InventoryRevision, slot.Value.Index))) return;
        _localCommandId = commandId;
    }

    private void ExecuteUseInventoryItem(UseInventoryItemCommand command)
    {
        var character = CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == command.CharacterId);
        if (character?.GetInventoryItem(InventorySlotKind.Backpack, command.BackpackIndex) is not MiscItemDefinition item ||
            item.Effect == ConsumableEffect.None || character.InventoryRevision != command.ExpectedInventoryRevision)
            return;

        var used = true;
        var result = item.Id == MiscItemIds.HerbalTea &&
                     (character.WaterLevel < 100 || character.IsAlive && character.CurrentVitality < character.MaximumVitality)
            ? UseHerbalTea(character, item.EffectValue)
            : IsInitiativeDrink(item) && character.IsAlive
                ? UseInitiativeDrink(character, item)
            : item.Effect switch
            {
                ConsumableEffect.Food when character.FoodLevel < 100 => UseFood(character, item.EffectValue),
                ConsumableEffect.Water when character.WaterLevel < 100 => UseWater(character, item.EffectValue),
                ConsumableEffect.Heal when character.IsAlive && character.CurrentVitality < character.MaximumVitality => UseHealing(character, item.EffectValue),
                ConsumableEffect.RestoreMana when character.IsAlive && character.UsesMana && character.CurrentMana < character.MaximumMana => UseManaPotion(character, item.EffectValue),
                ConsumableEffect.CurePoison when character.RemoveStatus(CharacterStatusIds.Poisoned) => "a mérgezés megszűnt",
                ConsumableEffect.CureDisease when character.RemoveStatus(CharacterStatusIds.Diseased) => "a betegség megszűnt",
                ConsumableEffect.StopBleeding when character.RemoveStatus(CharacterStatusIds.Bleeding) => "a vérzés elállt",
                ConsumableEffect.Vision when character.IsAlive => UseVisionItem(character, item),
                _ => string.Empty
            };
        if (string.IsNullOrEmpty(result)) used = false;
        if (!used) { _renderer.DrawInventoryMessage("A tárgy hatására most nincs szükség vagy nem alkalmazható.", ConsoleColor.DarkYellow); return; }

        character.RemoveOneInventoryItem(InventorySlotKind.Backpack, command.BackpackIndex);
        character.SynchronizeNeedStatuses(_gameData.GetStatus(CharacterStatusIds.Hungry), _gameData.GetStatus(CharacterStatusIds.Thirsty));
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        var message = $"{character.Name} használta: {item.Name} — {result}.";
        _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
        RecordSessionActivity(SessionActivityKind.System, message, ConsoleColor.Green, [character.Id]);
        if (item.Effect == ConsumableEffect.Heal)
            PlaySessionSound(SoundEffect.DefensiveSpell, [character.Id]);
    }

    private void ExecuteDropInventoryItem(DropInventoryItemCommand command)
    {
        var character = CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == command.CharacterId);
        if (character is null || character.InventoryRevision != command.ExpectedInventoryRevision) return;
        var item = character.GetInventoryItem(command.SlotKind, command.SlotIndex);
        if (item is null || SpellcastingRules.IsSpellcastingFocus(item) || CharacterBoundItemRules.IsBound(item)) return;
        var charges = character.GetInventoryItemCharges(command.SlotKind, command.SlotIndex);
        var position = GetCharacterWorldPosition(character);
        if (position is null || !character.RemoveOneInventoryItem(command.SlotKind, command.SlotIndex)) return;
        _maze.DropItem(position.Value, item, charges);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        var pileCount = _maze.GetGroundItemPileAt(position.Value)?.Items.Count ?? 1;
        _renderer.DrawInventoryMessage($"Ledobtad: {item.Name}. A mezőn {pileCount} tárgy van.", ConsoleColor.Cyan);
        PlaySessionSound(SoundEffect.Item, [character.Id]);
    }

    private void ExecutePickUpGroundItem(PickUpGroundItemCommand command)
    {
        var character = CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == command.CharacterId);
        var pile = _maze.GroundItemPiles.FirstOrDefault(candidate => candidate.Id == command.GroundPileId);
        var position = character is null ? null : GetCharacterWorldPosition(character);
        if (character is null || pile is null || position != pile.Position ||
            character.InventoryRevision != command.ExpectedInventoryRevision ||
            pile.Revision != command.ExpectedGroundPileRevision || command.GroundItemIndex < 0 ||
            command.GroundItemIndex >= pile.Entries.Count)
            return;
        var entry = pile.Entries[command.GroundItemIndex];
        var destinationItem = character.GetInventoryItem(InventorySlotKind.Backpack,
            command.DestinationBackpackIndex);
        var destinationQuantity = character.GetInventoryItemQuantity(InventorySlotKind.Backpack,
            command.DestinationBackpackIndex);
        if (destinationItem is not null && (!string.Equals(destinationItem.Id, entry.Item.Id,
                StringComparison.OrdinalIgnoreCase) ||
            character.GetInventoryItemCharges(InventorySlotKind.Backpack, command.DestinationBackpackIndex) !=
            entry.Charges || destinationQuantity >= LiveCharacter.MaximumBackpackStackSize)) return;
        var change = new InventorySlotChange(InventorySlotKind.Backpack, command.DestinationBackpackIndex,
            entry.Item, entry.Charges, destinationQuantity + 1);
        if (!character.CanApplyInventoryChanges(change) ||
            !pile.TryTake(command.GroundItemIndex, command.ExpectedGroundPileRevision, out _)) return;
        character.ApplyInventoryChanges(change);
        if (pile.Entries.Count == 0) _maze.RemoveGroundItemPile(pile);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        _renderer.DrawInventoryMessage($"Felvetted: {entry.Item.Name}.", ConsoleColor.Green);
        PlaySessionSound(SoundEffect.Item, [character.Id]);
    }

    private Position? GetCharacterWorldPosition(LiveCharacter character)
    {
        if (character == SelectedCharacter) return _player.Position;
        return _maze.PartyMembers.FirstOrDefault(member => member.Character == character)?.Position;
    }

    private static string UseFood(LiveCharacter character, int amount)
    {
        var before = character.FoodLevel;
        character.RestoreFood(amount);
        return $"élelem +{character.FoodLevel - before}";
    }

    private static string UseWater(LiveCharacter character, int amount)
    {
        var before = character.WaterLevel;
        character.RestoreWater(amount);
        return $"víz +{character.WaterLevel - before}";
    }

    private string UseHerbalTea(LiveCharacter character, int waterAmount)
    {
        var waterBefore = character.WaterLevel;
        var vitalityBefore = character.CurrentVitality;
        character.RestoreWater(waterAmount);
        var healing = _random.Next(5, 16);
        if (character.IsAlive) character.RestoreVitality(healing);
        return $"víz +{character.WaterLevel - waterBefore}, {FormatHealingResult(character, healing, vitalityBefore)}";
    }

    private static bool IsInitiativeDrink(MiscItemDefinition item) =>
        item.Id is MiscItemIds.Mead or MiscItemIds.SpicedWine;

    private static string UseInitiativeDrink(LiveCharacter character, MiscItemDefinition item)
    {
        var waterBefore = character.WaterLevel;
        character.RestoreWater(item.EffectValue);
        character.ApplySpellEffect(new ActiveSpellEffect(item.Id, ActiveSpellEffectType.InitiativeBonus,
            2, 10, Beneficial: true));
        character.ApplySpellEffect(new ActiveSpellEffect(item.Id, ActiveSpellEffectType.HitBonus,
            1, 10, Beneficial: true));
        return $"víz +{character.WaterLevel - waterBefore}, +2 kezdeményezés és +1 találat 10 akcióig";
    }

    private string UseVisionItem(LiveCharacter character, MiscItemDefinition item)
    {
        character.ApplySpellEffect(new ActiveSpellEffect(item.Id, ActiveSpellEffectType.VisionBonus,
            item.EffectValue, 12, Beneficial: true));
        if (GetCharacterWorldPosition(character) is { } position) RevealFor(character, position);
        return $"látótáv +{item.EffectValue} 12 akcióig";
    }

    private static string UseHealing(LiveCharacter character, int amount)
    {
        var before = character.CurrentVitality;
        character.RestoreVitality(amount);
        return FormatHealingResult(character, amount, before);
    }

    private static string FormatHealingResult(LiveCharacter character, int requestedAmount, int vitalityBefore)
    {
        var actual = character.CurrentVitality - vitalityBefore;
        var adjusted = character.PreviewVitalityRecovery(requestedAmount);
        var penalties = character.Statuses
            .Where(status => status.VitalityRecoveryPercent < 100)
            .Select(status => $"{status.Icon} {status.VitalityRecoveryPercent}%")
            .ToArray();
        var reduction = adjusted < requestedAmount && penalties.Length > 0
            ? $" (állapotok csökkentették: {requestedAmount} → {adjusted}; {string.Join(" × ", penalties)})"
            : string.Empty;
        return $"❤️ +{actual} HP{reduction}";
    }

    private static string UseManaPotion(LiveCharacter character, int amount)
    {
        var before = character.CurrentMana;
        character.RestoreMana(amount);
        return $"manna +{character.CurrentMana - before}";
    }

    private static string NpcBehaviorName(NpcBehavior? behavior) => behavior switch
    {
        NpcBehavior.Defensive => "Defenzív",
        NpcBehavior.Aggressive => "Aggresszív",
        NpcBehavior.Scout => "Felderítő",
        NpcBehavior.Cautious => "Óvatos",
        _ => "inaktív"
    };

    private void GrabOrPlaceInventoryItem()
    {
        var slot = _renderer.GetSelectedInventorySlot();
        if (slot is null) { _renderer.DrawInventoryMessage("Válassz egy felszerelés- vagy hátizsákhelyet.", ConsoleColor.DarkYellow); return; }
        var target = slot.Value;
        if (_heldInventoryItem is null)
        {
            var item = target.Character.GetInventoryItem(target.Kind, target.Index);
            if (item is null) { _renderer.DrawInventoryMessage("A kijelölt hely üres.", ConsoleColor.DarkYellow); return; }
            if (SpellcastingRules.IsSpellcastingFocus(item))
            { _renderer.DrawInventoryMessage($"A(z) {item.Name} a hátizsák első helyéhez kötött, ezért nem mozgatható.", ConsoleColor.Red); return; }
            _heldInventoryItem = new HeldInventoryItem(item, target, target.Character.InventoryRevision);
            _renderer.DrawInventoryMessage($"Kézben: {item.Name}. Válassz célhelyet, majd nyomj Space-t.", ConsoleColor.Yellow);
            return;
        }

        var held = _heldInventoryItem;
        if (target == held.Source)
        {
            _heldInventoryItem = null;
            _renderer.DrawInventoryMessage($"A(z) {held.Item.Name} áthelyezése megszakítva.", ConsoleColor.DarkYellow);
            return;
        }
        var commandId = _localCommandId + 1;
        var command = new InventoryTransferCommand(_session.HostPlayerId, commandId, held.Source.Character.Id,
            held.SourceRevision, held.Source.Kind, held.Source.Index, target.Character.Id,
            target.Character.InventoryRevision, target.Kind, target.Index);
        if (!_session.Submit(command)) return;
        _localCommandId = commandId;
        _heldInventoryItem = null;
    }

    private void CancelHeldInventoryItem()
    {
        if (_heldInventoryItem is not { } held) return;
        _heldInventoryItem = null;
        _renderer.DrawInventoryMessage($"A(z) {held.Item.Name} áthelyezése megszakítva.", ConsoleColor.DarkYellow);
    }

    private void SplitSelectedInventoryStack()
    {
        if (_heldInventoryItem is not null)
        {
            _renderer.DrawInventoryMessage("Előbb fejezd be vagy szakítsd meg a kézben tartott tárgy mozgatását.",
                ConsoleColor.DarkYellow);
            return;
        }
        var slot = _renderer.GetSelectedInventorySlot();
        if (slot is null || slot.Value.Kind != InventorySlotKind.Backpack)
        {
            _renderer.DrawInventoryMessage("Hátizsákban levő köteget jelölj ki a felezéshez.",
                ConsoleColor.DarkYellow);
            return;
        }
        var selected = slot.Value;
        var commandId = _localCommandId + 1;
        var command = new SplitInventoryStackCommand(_session.HostPlayerId, commandId,
            selected.Character.Id, selected.Character.InventoryRevision, selected.Index);
        if (!InventoryStackService.Validate(CharacterRoster.Party, command, out var error))
        {
            _renderer.DrawInventoryMessage(error, ConsoleColor.Red);
            return;
        }
        if (!_session.Submit(command)) return;
        _localCommandId = commandId;
    }

    private void ExecuteSplitInventoryStack(SplitInventoryStackCommand command)
    {
        if (!InventoryStackService.TryExecute(CharacterRoster.Party, command, out var result, out var error))
        {
            _renderer.DrawInventoryMessage(error, ConsoleColor.Red);
            return;
        }
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawInventoryMessage(
            $"Köteg megfelezve: {result.ItemName} ({result.RemainingQuantity}+{result.NewQuantity}).",
            ConsoleColor.Green);
        PlaySessionSound(SoundEffect.Item, [command.CharacterId]);
    }

    private void DistributeSelectedInventoryStack()
    {
        if (_heldInventoryItem is not null)
        {
            _renderer.DrawInventoryMessage("Előbb fejezd be vagy szakítsd meg a kézben tartott tárgy mozgatását.",
                ConsoleColor.DarkYellow);
            return;
        }
        var slot = _renderer.GetSelectedInventorySlot();
        if (slot is null || slot.Value.Kind != InventorySlotKind.Backpack)
        {
            _renderer.DrawInventoryMessage("Elfogyasztható hátizsáktárgyat jelölj ki a szétosztáshoz.",
                ConsoleColor.DarkYellow);
            return;
        }
        var selected = slot.Value;
        var commandId = _localCommandId + 1;
        var command = new DistributeInventoryStackCommand(_session.HostPlayerId, commandId,
            selected.Character.Id, selected.Character.InventoryRevision, selected.Index);
        if (!InventoryDistributionService.Validate(CharacterRoster.Party, command, out var error))
        {
            _renderer.DrawInventoryMessage(error, ConsoleColor.Red);
            return;
        }
        if (!_session.Submit(command)) return;
        _localCommandId = commandId;
    }

    private void ExecuteDistributeInventoryStack(DistributeInventoryStackCommand command)
    {
        if (!InventoryDistributionService.TryExecute(CharacterRoster.Party, command, out var result, out var error))
        {
            _renderer.DrawInventoryMessage(error, ConsoleColor.Red);
            return;
        }
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        var recipients = result.RecipientNames.Count == 0 ? string.Empty :
            $" → {string.Join(", ", result.RecipientNames)}";
        _renderer.DrawInventoryMessage(
            $"Szétosztva: {result.ItemName}, {result.DistributedQuantity} db{recipients}. " +
            $"A forráshelyen maradt: {result.RemainingSourceQuantity} db.", ConsoleColor.Green);
        RecordSessionActivity(SessionActivityKind.System,
            $"{result.ItemName} szétosztva a partyban ({result.DistributedQuantity} db).", ConsoleColor.Green);
        PlaySessionSound(SoundEffect.Item);
    }

    private bool TryStartAdHocFollowerConversation(DateTime now)
    {
        if (_session.Phase != GameSessionPhase.Exploration || _characterSheetFocused ||
            _activeTeamBattle is not null || _activeNarrative is not null ||
            _adHocConversationMazeLevel == _mazeLevel ||
            now - _lastAdHocConversationUtc < TimeSpan.FromHours(1) ||
            _maze.Enemies.Any(enemy => _fogOfWar.IsEnemyVisible(enemy.Id, enemy.Position)) ||
            _random.Next(100) >= 15) return false;

        var candidates = GetAdHocConversationCandidates().OrderBy(_ => _random.Next()).ToArray();
        foreach (var npc in candidates)
        {
            var starts = Enumerable.Range(1, 5).Select(index => $"ADHOC_{index}_START")
                .Where(state => !_usedAdHocConversationIds.Contains(AdHocConversationId(npc, state)) &&
                                _gameData.GetNpcStoryChoices(npc.StoryId!, state, npc.Friendliness).Count == 2)
                .OrderBy(_ => _random.Next()).ToArray();
            if (starts.Length == 0) continue;
            var state = starts[0];
            _usedAdHocConversationIds.Add(AdHocConversationId(npc, state));
            _lastAdHocConversationUtc = now;
            _adHocConversationMazeLevel = _mazeLevel;
            RunAdHocFollowerConversation(npc, state);
            return true;
        }
        return false;
    }

    private IReadOnlyList<WorldNpc> GetAdHocConversationCandidates() =>
        _storyConversationCoordinator.GetAdHocConversationCandidates(_maze, _player, CharacterRoster, SelectedCharacter);

    private static bool IsAdHocConversationStory(string? storyId) =>
        StoryConversationCoordinator.IsAdHocConversationStory(storyId);

    private static string AdHocConversationId(WorldNpc npc, string startState) =>
        StoryConversationCoordinator.AdHocConversationId(npc, startState);

    private void RunAdHocFollowerConversation(WorldNpc npc, string startState)
    {
        var previousPhase = _session.Phase;
        var conversationId = Guid.NewGuid();
        var transcript = new List<string>();
        var state = startState;
        _session.SetPhase(GameSessionPhase.Paused);
        try
        {
            while (true)
            {
                var choices = _gameData.GetNpcStoryChoices(npc.StoryId!, state, npc.Friendliness);
                if (choices.Count != 2) return;
                _activeAdHocConversation = new AdHocConversationSnapshot(conversationId, npc.Character.Name,
                    npc.Character.Race.Name, npc.Character.CharacterClass.Name, transcript.ToArray(),
                    choices[0].Prompt, choices.Select(choice => choice.Text).ToArray());
                _activeCoopHost?.TryPublish(CreateSessionSnapshot());
                var index = _renderer.DrawUniqueNpcStoryChoice(npc, choices[0].Prompt,
                    choices.Select(choice => choice.Text).ToArray(), transcript);
                var selected = choices[index];
                transcript.Add($"Te: {selected.Text}");
                transcript.AddRange(selected.Response.Split('|',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (selected.ContinueConversation)
                {
                    state = selected.NextStateId;
                    continue;
                }
                _activeAdHocConversation = new AdHocConversationSnapshot(conversationId, npc.Character.Name,
                    npc.Character.Race.Name, npc.Character.CharacterClass.Name, transcript.ToArray(), string.Empty, []);
                _activeCoopHost?.TryPublish(CreateSessionSnapshot());
                _renderer.DrawUniqueNpcStoryResponse(npc, transcript);
                return;
            }
        }
        finally
        {
            _activeAdHocConversation = null;
            _session.SetPhase(previousPhase);
            _activeCoopHost?.TryPublish(CreateSessionSnapshot());
            _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
            _renderer.SetCharacterSheetFocused(_characterSheetFocused);
        }
    }

    private void GiveSelectedStackToFollower()
    {
        if (_heldInventoryItem is not null)
        {
            _renderer.DrawInventoryMessage("Előbb fejezd be vagy szakítsd meg a tárgy mozgatását.", ConsoleColor.DarkYellow);
            return;
        }
        var slot = _renderer.GetSelectedInventorySlot();
        if (slot is null || slot.Value.Kind != InventorySlotKind.Backpack)
        {
            _renderer.DrawInventoryMessage("Elfogyasztható hátizsákköteget jelölj ki az átadáshoz.", ConsoleColor.DarkYellow);
            return;
        }
        var follower = _maze.PartyMembers.FirstOrDefault(member => member.IsTemporaryFollower && member.Character.IsAlive)
            ?.Character;
        if (follower is null)
        {
            _renderer.DrawInventoryMessage("Nincs aktív követő NPC, akinek átadhatnád.", ConsoleColor.DarkYellow);
            return;
        }
        var selected = slot.Value;
        var commandId = _localCommandId + 1;
        var command = new GiveFollowerStackCommand(_session.HostPlayerId, commandId, selected.Character.Id,
            selected.Character.InventoryRevision, selected.Index, follower.Id, follower.InventoryRevision);
        if (!_session.Submit(command)) return;
        _localCommandId = commandId;
    }

    private void ExecuteGiveFollowerStack(GiveFollowerStackCommand command)
    {
        var source = CharacterRoster.Party.Members.FirstOrDefault(character => character.Id == command.CharacterId);
        var follower = _maze.PartyMembers.FirstOrDefault(member => member.IsTemporaryFollower &&
            member.Character.Id == command.FollowerCharacterId)?.Character;
        var error = "A követő már nincs a csapattal.";
        if (source is null || follower is null || !FollowerStackTransferService.TryExecute(source, follower, command,
                out var result, out error))
        {
            _renderer.DrawInventoryMessage(error, ConsoleColor.Red);
            return;
        }
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        var message = $"{result.FollowerName} kapott: {result.ItemName} ×{result.TransferredQuantity}; " +
                      $"a forrásnál maradt: {result.RemainingQuantity}.";
        _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
        RecordSessionActivity(SessionActivityKind.System, message, ConsoleColor.Green);
        PlaySessionSound(SoundEffect.Item);
    }

    private void ExecuteInventoryTransfer(InventoryTransferCommand command)
    {
        if (!InventoryTransferService.TryExecute(CharacterRoster.Party, command, out var result, out var error))
        {
            _renderer.DrawInventoryMessage(error, ConsoleColor.Red);
            return;
        }
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawInventoryMessage(result.DisplacedItemName is null
            ? $"Áthelyezted: {result.SourceItemName}."
            : $"Felcserélted: {result.SourceItemName} ↔ {result.DisplacedItemName}.", ConsoleColor.Green);
        if (command.SenderId != _session.HostPlayerId && CharacterRoster.Party.Leader is { } leader)
        {
            var guestCharacter = _session.CharacterControls
                .Where(control => control.AssignedPlayerId == command.SenderId &&
                                  control.ConnectionState == PlayerConnectionState.Connected)
                .Select(control => CharacterRoster.Party.Members.FirstOrDefault(character =>
                    character.Id == control.CharacterId))
                .FirstOrDefault(character => character is not null);
            string? hostTransferMessage = null;
            if (command.DestinationCharacterId == leader.Id && command.CharacterId != leader.Id)
                hostTransferMessage = $"{guestCharacter?.Name ?? "A vendég"} átadta a hostnak: " +
                                      $"{result.SourceItemName}.";
            else if (command.CharacterId == leader.Id && command.DestinationCharacterId != leader.Id)
                hostTransferMessage = $"{guestCharacter?.Name ?? "A vendég"} elvette a hosttól: " +
                                      $"{result.SourceItemName}.";
            if (hostTransferMessage is not null)
            {
                _renderer.DrawInventoryMessage(hostTransferMessage, ConsoleColor.Yellow);
                RecordSessionActivity(SessionActivityKind.System, hostTransferMessage, ConsoleColor.Yellow,
                    [leader.Id]);
            }
        }
        PlaySessionSound(SoundEffect.Item, [command.CharacterId]);
    }

#endregion

    private void MoveEnemies()
    {
        var now = DateTime.UtcNow;
        foreach (var enemy in _maze.Enemies.Where(enemy => _nextEnemyMoves.GetValueOrDefault(enemy) <= now)
                     .OrderBy(_ => _random.Next()).ToArray())
        {
            ScheduleNextEnemyMove(enemy, now);
            var spellTick = enemy.AdvanceSpellEffects(_random);
            if (spellTick.Damage > 0)
            {
                var spellNotes = new List<string>();
                ApplyExplorationSpellDamage(SelectedCharacter, enemy, spellTick.Damage, spellNotes);
                _renderer.DrawInventoryMessage(string.Join("; ", spellTick.Notes.Concat(spellNotes)), ConsoleColor.Magenta);
                if (enemy.CurrentHitPoints <= 0) continue;
            }
            if (spellTick.SkipAction) continue;
            var visibleTarget = FindVisibleEnemyTarget(enemy);
            if (visibleTarget is not null)
            {
                if (enemy.PursuitState != EnemyPursuitState.Pursuing ||
                    enemy.PursuitTargetCharacterId != visibleTarget.Value.Character.Id ||
                    enemy.SearchRole != EnemySearchRole.None)
                    AlertEnemyGroup(enemy, visibleTarget.Value.Character.Id, visibleTarget.Value.Position);
                else
                    enemy.RefreshKnownTarget(visibleTarget.Value.Position);
            }
            if (enemy.ConsumeReactionDelay()) continue;

            Direction? direction;
            if (enemy.PursuitState == EnemyPursuitState.Pursuing)
            {
                var targetPosition = visibleTarget?.Position ?? enemy.LastKnownTargetPosition;
                direction = targetPosition is { } target && target != enemy.Position
                    ? FindEnemyStepToward(enemy, target)
                    : null;
                if (direction is null && visibleTarget is null)
                {
                    BeginEnemyGroupSearch(enemy);
                    direction = EnemySearchOrReturnDirection(enemy);
                }
            }
            else if (enemy.SearchRole != EnemySearchRole.None)
                direction = EnemySearchOrReturnDirection(enemy);
            else
                direction = enemy.Alertness == EnemyAlertness.Sleeping ? null : enemy.MovementProfile switch
                {
                    EnemyMovementProfile.Stationary => null,
                    EnemyMovementProfile.Patrol => enemy.PatrolDirection,
                    _ => Directions[_random.Next(Directions.Length)]
                };
            if (direction is null) continue;
            if (TryMoveEnemy(enemy, direction.Value))
            {
                if (_battleStarted) return;
                if (enemy.SearchRole == EnemySearchRole.Scout && !enemy.RecordSearchStep())
                    enemy.BeginReturn(0);
                else if (enemy.SearchRole == EnemySearchRole.Returning &&
                         Manhattan(enemy.Position, enemy.HomePosition) <= 1)
                    enemy.CompleteReturn();
                continue;
            }
            if (enemy.PursuitState != EnemyPursuitState.Pursuing && enemy.MovementProfile == EnemyMovementProfile.Patrol)
            {
                enemy.ReversePatrolDirection();
                if (TryMoveEnemy(enemy, enemy.PatrolDirection) && _battleStarted) return;
            }
        }
    }

    private (LiveCharacter Character, Position Position)? FindVisibleEnemyTarget(Enemy enemy)
    {
        return EnemyTargeting.ChooseNearestVisible(enemy.Position, LivingPartyWithPositions().ToArray(),
            position => FogOfWar.CanSee(_maze, enemy.Position, position, enemy.EffectiveVisionRange), _random);
    }

    private void AlertEnemyGroup(Enemy observer, CharacterId targetCharacterId, Position targetPosition)
    {
        foreach (var enemy in EnemyGroup(observer))
        {
            var reactionDelay = enemy.Alertness switch
            {
                EnemyAlertness.Sleeping => _random.Next(4, 9),
                EnemyAlertness.Drowsy => _random.Next(2, 5),
                _ => 0
            };
            enemy.BeginPursuit(targetCharacterId, targetPosition, reactionDelay);
        }
    }

    private IReadOnlyList<Enemy> EnemyGroup(Enemy member) => member.GroupId is null
        ? [member]
        : _maze.Enemies.Where(enemy => string.Equals(enemy.GroupId, member.GroupId,
            StringComparison.Ordinal)).ToList();

    private void BeginEnemyGroupSearch(Enemy observer)
    {
        var group = EnemyGroup(observer)
            .Where(enemy => enemy.SearchRole == EnemySearchRole.None)
            .OrderBy(_ => _random.Next()).ToList();
        if (group.Count == 0) return;
        var scoutCount = group.Count >= 3 && _random.Next(2) == 1 ? 2 : 1;
        foreach (var scout in group.OrderByDescending(enemy => enemy.EffectiveSpeed).Take(scoutCount))
            scout.BeginSearch(_random.Next(Enemy.MinimumSearchMoves, Enemy.MaximumSearchMoves + 1));
        foreach (var returning in group.Where(enemy => enemy.SearchRole != EnemySearchRole.Scout))
            returning.BeginReturn(_random.Next(5, 16));
    }

    private Direction? EnemySearchOrReturnDirection(Enemy enemy)
    {
        if (enemy.SearchRole == EnemySearchRole.Returning)
        {
            if (enemy.ConsumeReturnDelay()) return null;
            if (Manhattan(enemy.Position, enemy.HomePosition) <= 1)
            {
                enemy.CompleteReturn();
                return null;
            }
            return FindEnemyStepToward(enemy, enemy.HomePosition);
        }
        if (enemy.SearchRole != EnemySearchRole.Scout) return null;

        var directions = Directions.Where(direction => CanEnemySearchStep(enemy, direction)).ToList();
        if (directions.Count == 0)
        {
            enemy.BeginReturn(0);
            return EnemySearchOrReturnDirection(enemy);
        }
        var reverse = Opposite(enemy.PatrolDirection);
        var forwardChoices = directions.Where(direction => direction != reverse).ToList();
        var choices = forwardChoices.Count > 0 ? forwardChoices : directions;
        var selected = choices[_random.Next(choices.Count)];
        enemy.RememberTravelDirection(selected);
        return selected;
    }

    private bool CanEnemySearchStep(Enemy enemy, Direction direction)
    {
        var position = enemy.Position + direction;
        if (!_maze.IsWalkable(position) || position == _maze.Entrance || position == _maze.Exit) return false;
        if (position == _player.Position || _maze.GetPartyMemberAt(position) is not null) return true;
        var occupant = _maze.GetObjectAt(position);
        return occupant is null or GroundItemPile or Corpse || Maze.IsPassableNeutralNpc(occupant);
    }

    private static Direction Opposite(Direction direction) => direction switch
    {
        Direction.Up => Direction.Down,
        Direction.Down => Direction.Up,
        Direction.Left => Direction.Right,
        Direction.Right => Direction.Left,
        _ => Direction.Right
    };

    private void InitializeEnemyMoveSchedule(DateTime from)
    {
        _nextEnemyMoves.Clear();
        foreach (var enemy in _maze.Enemies) ScheduleNextEnemyMove(enemy, from);
    }

    private void ScheduleNextEnemyMove(Enemy enemy, DateTime from) =>
        _nextEnemyMoves[enemy] = from + EnemyMoveInterval(enemy);

    private static TimeSpan EnemyMoveInterval(Enemy enemy)
    {
        var speed = Math.Max(1, enemy.EffectiveSpeed);
        return TimeSpan.FromMilliseconds((double)ZombieMoveIntervalMilliseconds * ZombieSpeed / speed);
    }

    private bool TryMoveEnemy(Enemy enemy, Direction direction)
    {
        var previousPosition = enemy.Position;
        var destination = previousPosition + direction;
        if (_maze.GetPartyMemberAt(destination) is { } encounteredMember)
        {
            StartBattle(encounteredMember, enemy, enemyStrikesFirst: true);
            return true;
        }
        if (destination == _player.Position)
        {
            StartBattle(enemy, enemyStrikesFirst: true);
            return true;
        }
        if (!_maze.TryMoveEnemy(enemy, destination)) return false;
        RevealFor(SelectedCharacter, _player.Position);
        _renderer.DrawEnemyMovement(_maze, _fogOfWar, previousPosition, enemy.Position, _player.Position);
        return true;
    }

    private Direction? FindEnemyStepToward(Enemy enemy, Position target)
    {
        var queue = new Queue<Position>();
        var previous = new Dictionary<Position, Position>();
        queue.Enqueue(enemy.Position);
        previous[enemy.Position] = enemy.Position;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target) break;
            foreach (var direction in Directions)
            {
                var next = current + direction;
                if (previous.ContainsKey(next) || !CanEnemyPathThrough(next, target)) continue;
                previous[next] = current;
                queue.Enqueue(next);
            }
        }
        if (!previous.ContainsKey(target)) return null;
        var step = target;
        while (previous[step] != enemy.Position) step = previous[step];
        return Directions.First(direction => enemy.Position + direction == step);
    }

    private bool CanEnemyPathThrough(Position position, Position target)
    {
        if (position == target) return _maze.IsWalkable(position);
        if (!_maze.IsWalkable(position) || position == _maze.Entrance || position == _maze.Exit) return false;
        var occupant = _maze.GetObjectAt(position);
        return occupant is null or GroundItemPile or Corpse or PartyMemberAvatar ||
               Maze.IsPassableNeutralNpc(occupant);
    }

    private void MovePartyMembers()
    {
        var now = DateTime.UtcNow;
        NormalizeFormation();
        if (_formation.State == PartyFormationState.Assembling)
        {
            AdvanceFormationAssembly(now);
            return;
        }
        if (_partyScatterUntil is { } scatterUntil && now >= scatterUntil)
        {
            _partyScatterUntil = null;
            _renderer.DrawDeveloperMessage(_partyHoldingPosition
                ? "A szétszóródás véget ért; a parti ismét helyben marad."
                : "A szétszóródás véget ért; a parti folytatja korábbi viselkedését.");
        }
        var isScattering = _partyScatterUntil is not null;
        if (_partyRegrouping) isScattering = false;
        if (_partyHoldingPosition && !isScattering && !_partyRegrouping) return;
        foreach (var member in _maze.PartyMembers.ToArray())
        {
            if (_formation.State == PartyFormationState.Locked && !member.IsTemporaryFollower &&
                _formation.Slots.Contains(member.Character.Id)) continue;
            if (_session.IsHumanControlled(member.Character.Id)) continue;
            if (_nextPartyMoves.GetValueOrDefault(member) > now) continue;
            ScheduleNextPartyMove(member, now);
            // Allow NPCs to cast simple exploration spells (heals/cures) before moving
            TryNpcCastExplorationSpell(member);
            if (isScattering)
            {
                MovePartyMemberAwayFromLeader(member);
                continue;
            }
            if (_partyRegrouping)
            {
                MovePartyMemberTowardLeader(member);
                continue;
            }
            if (CanActivelyAttack(member) && TryResolveAdjacentNpcBattle(member))
            {
                if (_battleStarted) return;
                continue;
            }
            var previous = member.Position;
            var next = ChoosePartyMemberStep(member);
            if (next is null || !CanEnterTrap(member.Character, next.Value) ||
                !_maze.TryMovePartyMember(member, next.Value, _player.Position)) continue;
            member.Character.RegisterExplorationStep();
            var newlyRevealed = RevealFor(member.Character, member.Position, advanceEnemyMemory: true);
            _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, previous, member.Position, newlyRevealed, _player.Position);
            CheckBossDiscoveryAt(newlyRevealed, member.Character);
            TriggerTrapAt(member.Character, member.Position);
            if (CanActivelyAttack(member) && TryResolveAdjacentNpcBattle(member) && _battleStarted) return;
        }
    }

    private void AdvanceFormationAssembly(DateTime now)
    {
        var targets = PartyFormationController.Positions(_formation, SelectedCharacter.Id, _player.Position);
        var result = PartyFormationAssemblyCoordinator.Advance(
            now,
            _maze,
            _player,
            SelectedCharacter.Id,
            targets,
            _maze.PartyMembers,
            _nextPartyMoves,
            FindNextFormationAssemblyStep,
            (member, targetPosition) => CanEnterTrap(member.Character, targetPosition),
            (member, position) => _maze.GetPartyMemberAt(position),
            (member, nextPosition) => _maze.TryMovePartyMember(member, nextPosition, _player.Position),
            (member, blockingFriend, position) => _maze.TrySwapPartyMembers(member, blockingFriend, _player.Position),
            RegisterFormationAssemblyMove,
            ScheduleNextPartyMove,
            (member, nextPosition) => _maze.GetEnemyAt(nextPosition),
            (member, enemy) => StartBattle(member, enemy),
            FormationAvatar);

        if (result.BattleStarted) return;
        if (result.AllInPlace)
        {
            _formation = PartyFormationRules.WithState(_formation, PartyFormationState.Locked);
            _renderer.SetFormationStatus(_formation);
            _session.SetFormationMovementLocked(true);
            _formationObstacleReported = false;
            AnnouncePartyCommand("Az alakzat osszeallt. Csak a vezer mozgathatja; Ctrl+bal/jobb: fordulas.",
                ConsoleColor.Green);
            return;
        }
        if (!result.MadeProgress && !_formationObstacleReported && result.ObstacleReported)
        {
            _formationObstacleReported = true;
            _renderer.DrawDeveloperMessage("Az alakzat meg nem tud osszeallni: egy kijelolt hely nem erheto el.");
        }
    }

    private void RegisterFormationAssemblyMove(PartyMemberAvatar member, Position previous)
    {
        member.Character.RegisterExplorationStep();
        var newlyRevealed = RevealFor(member.Character, member.Position, advanceEnemyMemory: true);
        _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, previous, member.Position, newlyRevealed,
            _player.Position);
        CheckBossDiscoveryAt(newlyRevealed, member.Character);
        TriggerTrapAt(member.Character, member.Position);
    }

    private Position? FindNextFormationAssemblyStep(PartyMemberAvatar member, Position target,
        IReadOnlyDictionary<CharacterId, Position> formationTargets) =>
        PartyMovementController.FindNextFormationAssemblyStep(member, target, formationTargets, _maze, _player);

    private bool CanFormationAssemblyTraverse(PartyMemberAvatar member, Position position,
        IReadOnlyDictionary<CharacterId, Position> formationTargets) =>
        PartyMovementController.CanFormationAssemblyTraverse(member, position, formationTargets, _maze, _player);

    private void TogglePartyHoldPosition()
    {
        _partyCommandState = _partyCommandController.ToggleHoldPosition(_partyCommandState);
        _partyHoldingPosition = _partyCommandState.HoldingPosition;
        _partyRegrouping = _partyCommandState.Regrouping;
        _partyAttackMode = _partyCommandState.AttackMode;
        _partyScatterUntil = _partyCommandState.ScatterUntil;
        if (!_partyHoldingPosition)
            foreach (var member in _maze.PartyMembers) _nextPartyMoves[member] = DateTime.UtcNow;
        AnnouncePartyCommand(_partyHoldingPosition
            ? "✋ MEGÁLLJ: minden NPC társ azonnal tartja a helyét; a Támadás és Gyülekező kikapcsolt."
            : "✋ A Megállj parancs kikapcsolt; az NPC társak folytatják saját viselkedésüket.",
            _partyHoldingPosition ? ConsoleColor.Yellow : ConsoleColor.Gray);
    }

    private void TogglePartyRegrouping()
    {
        _partyCommandState = _partyCommandController.ToggleRegrouping(_partyCommandState);
        _partyHoldingPosition = _partyCommandState.HoldingPosition;
        _partyRegrouping = _partyCommandState.Regrouping;
        _partyAttackMode = _partyCommandState.AttackMode;
        _partyScatterUntil = _partyCommandState.ScatterUntil;
        foreach (var member in _maze.PartyMembers) _nextPartyMoves[member] = DateTime.UtcNow;
        AnnouncePartyCommand(_partyRegrouping
            ? "🛡️ GYÜLEKEZŐ: minden NPC társ harc keresése nélkül a vezér mellé zárkózik és ott marad; a Támadás és Megállj kikapcsolt."
            : "🛡️ A Gyülekező kikapcsolt; az NPC társak folytatják saját viselkedésüket.",
            _partyRegrouping ? ConsoleColor.Cyan : ConsoleColor.Gray);
    }

    private void TogglePartyAttackMode()
    {
        _partyCommandState = _partyCommandController.ToggleAttackMode(_partyCommandState);
        _partyHoldingPosition = _partyCommandState.HoldingPosition;
        _partyRegrouping = _partyCommandState.Regrouping;
        _partyAttackMode = _partyCommandState.AttackMode;
        _partyScatterUntil = _partyCommandState.ScatterUntil;
        foreach (var member in _maze.PartyMembers) _nextPartyMoves[member] = DateTime.UtcNow;
        AnnouncePartyCommand(_partyAttackMode
            ? "⚔️ TÁMADÁS: minden NPC társ agresszívan keresi és támadja az ellenfeleket a parancs kikapcsolásáig."
            : "⚔️ A Támadás kikapcsolt; az NPC társak visszatértek saját viselkedésükhöz.",
            _partyAttackMode ? ConsoleColor.Red : ConsoleColor.Gray);
    }

    private void AnnouncePartyCommand(string message, ConsoleColor color)
    {
        _renderer.DrawDeveloperMessage(message);
        RecordSessionActivity(SessionActivityKind.System, message, color);
    }

    private void ScatterPartyTemporarily()
    {
        _partyCommandState = _partyCommandController.ScatterTemporarily(_partyCommandState, DateTime.UtcNow);
        _partyHoldingPosition = _partyCommandState.HoldingPosition;
        _partyRegrouping = _partyCommandState.Regrouping;
        _partyAttackMode = _partyCommandState.AttackMode;
        _partyScatterUntil = _partyCommandState.ScatterUntil;
        foreach (var member in _maze.PartyMembers)
            _nextPartyMoves[member] = DateTime.UtcNow + TimeSpan.FromMilliseconds(_random.Next(0, 100));
        AnnouncePartyCommand("Partiparancs: szétszóródás 10 másodpercig; a Támadás, Gyülekező és Megállj kikapcsolt.", ConsoleColor.Magenta);
    }

    private void MovePartyMemberTowardLeader(PartyMemberAvatar member)
    {
        if (Manhattan(member.Position, _player.Position) <= 1) return;
        var next = FindNextStep(member, FreeNeighborsOf(_player.Position))
                   ?? FollowLeaderTrail(member, minimumLag: 1);
        if (next is null) return;
        var previous = member.Position;
        if (!CanEnterTrap(member.Character, next.Value) ||
            !_maze.TryMovePartyMember(member, next.Value, _player.Position)) return;
        member.Character.RegisterExplorationStep();
        var newlyRevealed = RevealFor(member.Character, member.Position, advanceEnemyMemory: true);
        _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, previous, member.Position, newlyRevealed,
            _player.Position);
        CheckBossDiscoveryAt(newlyRevealed, member.Character);
        TriggerTrapAt(member.Character, member.Position);
    }

    private void MovePartyMemberAwayFromLeader(PartyMemberAvatar member)
    {
        if (Manhattan(member.Position, _player.Position) >= 10) return;
        var target = FindReachablePositions(member, 12)
            .Where(entry => Manhattan(entry.Position, _player.Position) <= 10)
            .OrderByDescending(entry => Manhattan(entry.Position, _player.Position))
            .ThenBy(entry => entry.Distance)
            .FirstOrDefault();
        if (target == default) return;
        var next = FindNextStep(member, [target.Position]);
        if (next is null) return;
        var previous = member.Position;
        if (!CanEnterTrap(member.Character, next.Value) ||
            !_maze.TryMovePartyMember(member, next.Value, _player.Position)) return;
        member.Character.RegisterExplorationStep();
        var newlyRevealed = RevealFor(member.Character, member.Position, advanceEnemyMemory: true);
        _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, previous, member.Position, newlyRevealed, _player.Position);
        CheckBossDiscoveryAt(newlyRevealed, member.Character);
        TriggerTrapAt(member.Character, member.Position);
    }

    private bool CanActivelyAttack(PartyMemberAvatar member) =>
        _partyAiController.CanActivelyAttack(_partyAttackMode, member);

    private bool TryResolveAdjacentNpcBattle(PartyMemberAvatar member) =>
        _partyAiController.TryResolveAdjacentNpcBattle(_maze, member, (avatar, enemy) => StartBattle(avatar, enemy));

    private bool IsQuestCriticalRoderic(PartyMemberAvatar member) =>
        member.TemporaryFollower is { } follower &&
        string.Equals(follower.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(follower.StoryStateId, "JOINED", StringComparison.OrdinalIgnoreCase);

    private void ScheduleNextPartyMove(PartyMemberAvatar member, DateTime from) =>
        _partyAiController.ScheduleNextPartyMove(member, from, _player, _nextPartyMoves);

    private bool CanControlledCharacterMove(LiveCharacter character) =>
        _partyAiController.CanControlledCharacterMove(character, _nextControlledMoves);

    private void ScheduleNextControlledMove(LiveCharacter character) =>
        _partyAiController.ScheduleNextControlledMove(character, _nextControlledMoves);

    private Position? ChoosePartyMemberStep(PartyMemberAvatar member)
    {
        var effectiveMember = _partyAttackMode && member.Character.NpcBehavior != NpcBehavior.Aggressive
            ? new PartyMemberAvatar(member.Position, member.Character, member.TemporaryFollower)
            : member;
        if (_partyAttackMode && member.Character.NpcBehavior != NpcBehavior.Aggressive)
        {
            var original = member.Character.NpcBehavior;
            member.Character.SetNpcBehavior(NpcBehavior.Aggressive);
            var step = PartyMovementController.ChoosePartyMemberStep(member, _maze, _player, _leaderFacing, _leaderTrail, CurrentLevelVisionModifier);
            member.Character.SetNpcBehavior(original);
            return step;
        }
        return PartyMovementController.ChoosePartyMemberStep(member, _maze, _player, _leaderFacing, _leaderTrail, CurrentLevelVisionModifier);
    }

    private Position? FollowLeaderTrail(PartyMemberAvatar member, int minimumLag) =>
        PartyMovementController.FollowLeaderTrail(member, minimumLag, _maze, _player, _leaderTrail);

    private Position? ChooseForwardStep(PartyMemberAvatar member, int maximumLeaderDistance, int maximumSearchDistance, bool avoidNarrowFront) =>
        PartyMovementController.ChooseForwardStep(member, maximumLeaderDistance, maximumSearchDistance, avoidNarrowFront, _maze, _player, _leaderFacing);

    private Position? FindNextStep(PartyMemberAvatar member, IEnumerable<Position> targetPositions) =>
        PartyMovementController.FindNextStep(member, targetPositions, _maze, _player);

    private IReadOnlyList<(Position Position, int Distance)> FindReachablePositions(PartyMemberAvatar member, int maximumDistance) =>
        PartyMovementController.FindReachablePositions(member, maximumDistance, _maze, _player);

    private IEnumerable<Position> FreeNeighborsOf(Position origin) =>
        PartyMovementController.FreeNeighborsOf(_maze, _player, origin);

    private bool CanPartyTraverse(PartyMemberAvatar member, Position position) =>
        PartyMovementController.CanPartyTraverse(member, position, _maze, _player);

    private int CountWalkableNeighbors(Position position) =>
        PartyMovementController.CountWalkableNeighbors(position, _maze);

    private bool IsAheadOfLeader(Position position) =>
        PartyMovementController.IsAheadOfLeader(position, _player.Position, _leaderFacing);

    private static int Manhattan(Position first, Position second) => PartyMovementController.Manhattan(first, second);
    private static (int X, int Y) DirectionOffset(Direction direction) => PartyMovementController.DirectionOffset(direction);

    private void HandleLocalBattleInput(ConsoleKeyInfo key)
    {
        if (_activeTeamBattle is { } teamBattle) HandleLocalTeamBattleInput(teamBattle, key);
    }

    private void HandleLocalTeamBattleInput(TeamBattleEncounter battle, ConsoleKeyInfo key)
    {
        if (battle.IsCompleted) return;
        if (IsHelpShortcut(key))
        {
            ShowInGameHelp();
            _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
            ContinueTeamBattle();
            return;
        }
        if (IsSaveGameShortcut(key))
        {
            _saveAfterBattle = true;
            _renderer.DrawInventoryMessage("Mentés kérve: a csapatharc lezárása után elkészül.", ConsoleColor.Yellow);
            return;
        }
        if (battle.CurrentEnemy is not null)
        {
            if (key.Key == ConsoleKey.Spacebar)
                SubmitLocalBattleCommand(BattleActionKind.AdvanceEnemyTurn);
            return;
        }
        if (battle.CurrentCharacter is not { } character || character != SelectedCharacter) return;
        var enemy = battle.SelectedTargetEnemy() ?? ClosestLivingTeamEnemy(battle, GetCasterPosition(character));
        var allowed = GetTeamAllowedBattleActions(battle, character, enemy);
        if (battle.RuntimeFor(character).RequiresTacticSelection && key.Key is ConsoleKey.D1 or ConsoleKey.NumPad1 or
                ConsoleKey.D2 or ConsoleKey.NumPad2 or ConsoleKey.D3 or ConsoleKey.NumPad3)
        {
            var option = key.Key is ConsoleKey.D1 or ConsoleKey.NumPad1 ? 1 :
                key.Key is ConsoleKey.D2 or ConsoleKey.NumPad2 ? 2 : 3;
            SubmitLocalBattleCommand(TacticActionFor(character.CharacterClass.Id, option));
            return;
        }
        if (key.Key == ConsoleKey.Tab && allowed.Contains(BattleActionKind.SelectTarget) &&
            NextTeamBattleTarget(battle, character) is { } selectedTarget)
        {
            SubmitLocalBattleCommand(BattleActionKind.SelectTarget, targetEnemyId: selectedTarget.Id);
            return;
        }
        if (key.Key == ConsoleKey.R && allowed.Contains(BattleActionKind.Retreat))
        {
            SubmitLocalBattleCommand(BattleActionKind.Retreat);
            return;
        }
        if ((key.Key == ConsoleKey.P || key.Key == ConsoleKey.Spacebar && IsTeamMovementInProgress(battle)) &&
            allowed.Contains(BattleActionKind.Pass))
        {
            SubmitLocalBattleCommand(BattleActionKind.Pass);
            return;
        }
        if (key.Key == ConsoleKey.Spacebar && allowed.Contains(BattleActionKind.PhysicalAttack))
        {
            var targetEnemy = battle.SelectedTargetEnemy() ??
                              ReachableTeamEnemies(battle, character).OrderBy(value => value.CurrentHitPoints).First();
            SubmitLocalBattleCommand(BattleActionKind.PhysicalAttack, targetEnemyId: targetEnemy.Id);
            return;
        }
        if (key.Key == ConsoleKey.C && allowed.Contains(BattleActionKind.SwapWeapon))
        {
            SubmitLocalBattleCommand(BattleActionKind.SwapWeapon);
            return;
        }
        if (key.Key == ConsoleKey.H && allowed.Contains(BattleActionKind.SwapToRear))
        {
            SubmitLocalBattleCommand(BattleActionKind.SwapToRear);
            return;
        }
        if (TryGetDirection(key.Key, out var formationDirection) &&
            allowed.Contains(BattleActionKind.MoveFormation))
        {
            SubmitLocalBattleCommand(BattleActionKind.MoveFormation,
                target: GetCasterPosition(character) + formationDirection);
            return;
        }
        if (TryGetDirection(key.Key, out var direction) && allowed.Contains(BattleActionKind.Move))
        {
            SubmitLocalBattleCommand(BattleActionKind.Move, target: GetCasterPosition(character) + direction);
            return;
        }
        if (key.Key == ConsoleKey.U && allowed.Contains(BattleActionKind.UseItem))
        {
            var item = SelectTeamBattleItem(battle, character);
            if (item is not null)
                SubmitLocalBattleCommand(BattleActionKind.UseItem, backpackIndex: item.BackpackIndex);
            return;
        }
        if (key.Key == ConsoleKey.T && allowed.Contains(BattleActionKind.TurnUndead))
        {
            var undeadTarget = battle.SelectedTargetEnemy() is { } selected && CanTurnUndead(character, selected)
                ? selected
                : AdjacentTeamEnemies(battle, character).First(candidate => CanTurnUndead(character, candidate));
            SubmitLocalBattleCommand(BattleActionKind.TurnUndead,
                targetEnemyId: undeadTarget.Id);
            return;
        }

        SpellDefinition? spell = null;
        MagicItemDefinition? castingItem = null;
        int? castingItemSlotIndex = null;
        if (key.Key == ConsoleKey.V && allowed.Contains(BattleActionKind.CastSpell))
        {
            var selection = _renderer.DrawSpellCastingScreen([character], 0, inCombat: true, _maze, _fogOfWar,
                _ => GetCasterPosition(character), () => { });
            _renderer.RestoreSpellCastingOverlay();
            spell = selection?.Spell;
            castingItem = selection?.CastingItem;
            castingItemSlotIndex = selection?.CastingItemSlotIndex;
        }
        else if (TryGetQuickSpellIndex(key, out var slotIndex) && allowed.Contains(BattleActionKind.CastSpell))
            spell = character.QuickSpells[slotIndex];
        if (spell is null) return;
        var validation = ValidateSpellCast(character, GetCasterPosition(character), spell, inCombat: true,
            enemy, castingItem, castingItemSlotIndex);
        if (validation is not null)
        {
            _renderer.DrawInventoryMessage(validation.Message, ConsoleColor.Red);
            return;
        }
        var targetPosition = SelectSpellTarget(character, GetCasterPosition(character), spell, enemy);
        if (targetPosition is null) return;
        SubmitLocalBattleCommand(BattleActionKind.CastSpell, spell.Id, castingItemSlotIndex,
            targetPosition, enemy.Id);
    }

    private BattleItemOptionSnapshot? SelectTeamBattleItem(TeamBattleEncounter battle,
        LiveCharacter character)
    {
        var options = GetBattleItemOptions(battle, character).Take(9).ToArray();
        if (options.Length == 0) return null;
        _renderer.DrawInventoryMessage("Tárgyhasználat: " + string.Join(" | ", options.Select((item, index) =>
            $"{index + 1} - {item.Name} ×{item.Quantity}")) + " | Esc - mégse", ConsoleColor.Cyan);
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                _renderer.DrawInventoryMessage("Tárgyhasználat megszakítva.", ConsoleColor.DarkYellow);
                return null;
            }
            if (key.KeyChar is >= '1' and <= '9' && key.KeyChar - '1' < options.Length)
                return options[key.KeyChar - '1'];
        }
    }

    private void SubmitLocalBattleCommand(BattleActionKind action, string? spellId = null,
        int? castingItemSlotIndex = null, Position? target = null,
        WorldEntityId? targetEnemyId = null, int? backpackIndex = null)
    {
        var battleId = _activeTeamBattle?.Id;
        var turnId = _activeTeamBattle?.Turns.TurnId;
        if (battleId is null || turnId is null) return;
        var commandId = _localCommandId + 1;
        if (_session.Submit(new BattleActionCommand(_session.HostPlayerId, commandId, SelectedCharacter.Id,
                battleId.Value, turnId.Value, action, spellId, castingItemSlotIndex, target,
                targetEnemyId, backpackIndex)))
            _localCommandId = commandId;
    }

    private static BattleActionKind TacticActionFor(string characterClassId, int option) =>
        characterClassId == CharacterClassIds.Harcos
            ? option switch
            {
                1 => BattleActionKind.FighterPrecise,
                2 => BattleActionKind.FighterPowerful,
                _ => BattleActionKind.FighterDefensive
            }
            : option switch
            {
                1 => BattleActionKind.ThiefAmbush,
                2 => BattleActionKind.ThiefObserve,
                _ => BattleActionKind.ThiefPoison
            };

    private void ExecuteBattleAction(BattleActionCommand command)
    {
        if (_activeTeamBattle is { } teamBattle) ExecuteTeamBattleAction(teamBattle, command);
    }

    private LiveCharacter? TryRollKnightProtector(LiveCharacter protectedCharacter) =>
        _singleBattleCoordinator.TryRollKnightProtector(protectedCharacter, GetCasterPosition(protectedCharacter),
            LivingPartyWithPositions());

    private static BattleTactic ToBattleTactic(BattleActionKind action) => SingleBattleCoordinator.ToBattleTactic(action);

    private static string BattleTacticName(BattleTactic tactic, LiveCharacter character) =>
        SingleBattleCoordinator.BattleTacticName(tactic, character);

    private IReadOnlyList<BattleSpellOption> GetSpellOptions(LiveCharacter character,
        Position characterPosition, Enemy? enemy, bool inCombat) =>
        _singleBattleCoordinator.GetSpellOptions(character, characterPosition, enemy, inCombat,
            (c, pos, sp, en) => HasValidSpellTarget(c, pos, sp, en),
            (pos, sp, en) => GetValidSpellTargets(pos, sp, en));

    private void ExecuteExplorationSpell(CastExplorationSpellCommand command)
    {
        var character = CharacterRoster.Party.Members.FirstOrDefault(member => member.Id == command.CharacterId);
        if (character is null || !character.IsAlive) return;
        var spell = _gameData.Spells.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.SpellId, StringComparison.OrdinalIgnoreCase));
        if (spell is null)
        {
            _session.RejectExecutedCommand(command, "Ismeretlen varázslat.");
            return;
        }
        MagicItemDefinition? castingItem = null;
        if (command.CastingItemSlotIndex is { } slot)
            castingItem = character.MagicItems.ElementAtOrDefault(slot);
        var result = TryCastSpell(character, GetCasterPosition(character), spell, inCombat: false,
            currentEnemy: null, castingItem: castingItem,
            castingItemSlotIndex: command.CastingItemSlotIndex, explicitTarget: command.Target);
        if (result is null || !result.ConsumesTurn)
        {
            _session.RejectExecutedCommand(command, result?.Message ?? "A varázslat célpontja érvénytelen.");
            return;
        }
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawInventoryMessage(result.Message,
            result.Kind == BattleLogKind.Information ? ConsoleColor.Red : ConsoleColor.Magenta);
        RecordSessionActivity(SessionActivityKind.Spell, result.Message,
            result.Kind == BattleLogKind.Information ? ConsoleColor.Red : ConsoleColor.Magenta);
    }

    private bool HasUsableCombatSpell(LiveCharacter character, Position characterPosition, Enemy enemy) =>
        _singleBattleCoordinator.HasUsableCombatSpell(character, characterPosition, enemy,
            _timeStopUsedThisBattle, EquippedCastingItems(character),
            (c, pos, sp, en) => HasValidSpellTarget(c, pos, sp, en));

    private static bool CanTurnUndead(LiveCharacter character, Enemy enemy) =>
        SingleBattleCoordinator.CanTurnUndead(character, enemy);

    private BattlePlayerAction ResolveTurnUndead(LiveCharacter character, Enemy enemy) =>
        _singleBattleCoordinator.ResolveTurnUndead(character, enemy, _turnUndeadUsedThisBattle);

    private SpellCastAttempt? TryCastSpell(LiveCharacter caster, Position casterPosition, SpellDefinition spell,
        bool inCombat, Enemy? currentEnemy, MagicItemDefinition? castingItem = null, int? castingItemSlotIndex = null,
        Position? explicitTarget = null)
    {
        var validation = ValidateSpellCast(caster, casterPosition, spell, inCombat, currentEnemy, castingItem,
            castingItemSlotIndex, explicitTarget);
        if (validation is not null) return validation;
        var usingItem = castingItem is not null;
        var castingItemIndex = usingItem ? castingItemSlotIndex ?? -1 : -1;
        var manaCost = usingItem ? 0 : SpellcastingRules.EffectiveManaCost(caster, spell);
        var target = explicitTarget ?? SelectSpellTarget(caster, casterPosition, spell, currentEnemy);
        if (target is null) return null;
        var divineJudgment = !usingItem && caster.RecordDivineSpellCast(spell);
        if (usingItem)
        {
            caster.ConsumeMagicItemCharge(castingItemIndex);
            _renderer.RefreshCharacterSheet(SelectedCharacter);
        }
        else caster.SpendMana(manaCost);
        _renderer.RefreshBattleStatusRows();

        if (inCombat)
        {
            var engaged = _activeTeamBattle?.IsEngaged(caster) == true;
            var failureChance = SpellcastingRules.CombatFailureChance(caster, engaged);
            var roll = _random.Next(1, 101);
            if (roll <= failureChance)
                return new SpellCastAttempt(true,
                    $"{caster.Name} varázslata meghiúsul: {spell.Name} — kockázat {failureChance}%, dobás {roll}. " +
                    (engaged ? "Lekötés: +15%. " : string.Empty) +
                    (usingItem ? $"{CastingItemUseText(castingItem!)}; az akció elveszett." : $"-{manaCost} manna; az akció elveszett."),
                    BattleLogKind.Information);
        }

        if (IsOffensiveSpell(spell)) caster.BreakSanctuary();
        var spellListeners = ResolveCharacterSpellTargets(caster, spell, target.Value)
            .Select(character => character.Id)
            .Append(caster.Id)
            .Concat(inCombat ? [SelectedCharacter.Id] : [])
            .Distinct()
            .ToArray();
        PlaySessionSound(IsOffensiveSpell(spell) ? SoundEffect.OffensiveSpell : SoundEffect.DefensiveSpell,
            spellListeners);
        var targetText = DescribeSpellTarget(caster, spell, target.Value, currentEnemy);
        var execution = ExecuteSpell(caster, casterPosition, spell, target.Value, inCombat, currentEnemy, divineJudgment);
        var judgmentText = divineJudgment ? " ⚡ Isteni ítélet: kétszeres számszerű hatás és ingyenes varázslat." : string.Empty;
        return new SpellCastAttempt(true,
            $"{caster.Name} elsüti: {spell.Name} → {targetText}. " +
            (usingItem ? $"{CastingItemUseText(castingItem!)}; 0 manna." : $"-{manaCost} manna.") +
            $"{judgmentText} {execution.Summary}",
            BattleLogKind.PlayerAttack, execution.DamageToCurrentEnemy, execution.ExtraPlayerActions,
            execution.Details is { } detail ? detail with
            {
                Calculation = detail.Calculation.Prepend(usingItem ? "🔷 Tárgyhasználat: 0 manna" : $"🔷 Mannaköltség: {manaCost}").ToArray()
            } : null);
    }

    private IEnumerable<(LiveCharacter Character, Position Position)> LivingPartyWithPositions()
    {
        if (SelectedCharacter.IsAlive && _player is not null) yield return (SelectedCharacter, _player.Position);
        if (_maze is not null)
        {
            foreach (var member in _maze.PartyMembers.Where(member => member.Character.IsAlive))
                yield return (member.Character, member.Position);
        }
    }

    private SpellCastAttempt? ValidateSpellCast(LiveCharacter caster, Position casterPosition, SpellDefinition spell,
        bool inCombat, Enemy? currentEnemy, MagicItemDefinition? castingItem = null, int? castingItemSlotIndex = null,
        Position? explicitTarget = null) =>
        _spellExecutionService.ValidateSpellCast(caster, casterPosition, spell, inCombat, currentEnemy, castingItem,
            castingItemSlotIndex, explicitTarget, _timeStopUsedThisBattle, LivingPartyWithPositions().ToArray(),
            _maze, _fogOfWar, _player?.Position, SelectedCharacter);

    private bool IsValidExplicitSpellTarget(LiveCharacter caster, Position casterPosition, SpellDefinition spell,
        Position target, Enemy? currentEnemy) =>
        _spellExecutionService.IsValidExplicitSpellTarget(caster, casterPosition, spell, target, currentEnemy,
            LivingPartyWithPositions().ToArray(), _maze, _fogOfWar, _player?.Position, SelectedCharacter);

    private static string CastingItemUseText(MagicItemDefinition item) => SpellExecutionService.CastingItemUseText(item);

    private SpellExecutionResult ExecuteSpell(LiveCharacter caster, Position casterPosition, SpellDefinition spell, Position target, bool inCombat,
        Enemy? currentEnemy, bool divineJudgment) =>
        _spellExecutionService.ExecuteSpell(caster, casterPosition, spell, target, inCombat, currentEnemy, divineJudgment,
            ref _timeStopUsedThisBattle, LivingPartyWithPositions().ToArray(), _maze,
            ApplyExplorationSpellDamage, TeleportLeader, TeleportLivingParty, ResurrectPartyMember,
            c => _renderer.RefreshCharacterSheet(c));

    private IEnumerable<Enemy> ResolveEnemySpellTargets(SpellDefinition spell, Position target, Enemy? currentEnemy, Position casterPosition) =>
        _spellExecutionService.ResolveEnemySpellTargets(spell, target, currentEnemy, casterPosition, _maze);

    private IEnumerable<LiveCharacter> ResolveCharacterSpellTargets(LiveCharacter caster, SpellDefinition spell, Position target) =>
        _spellExecutionService.ResolveCharacterSpellTargets(caster, spell, target, LivingPartyWithPositions().ToArray(), _maze);

    private bool IsOffensiveSpell(SpellDefinition spell) => _spellExecutionService.IsOffensiveSpell(spell);

    private static bool IsUnholy(EnemyDefinition enemy) => SpellExecutionService.IsUnholy(enemy);

    private string ResurrectPartyMember(Position target, SpellEffectDefinition effect)
    {
        var corpse = _maze.Corpses.OfType<PartyMemberCorpse>().FirstOrDefault(candidate => candidate.Position == target);
        if (corpse is null) return "nincs feltámasztható társ a célmezőn";
        if (corpse.Character.WasResurrectedThisLevel) return $"{corpse.Character.Name} ezen a pályán már visszatért egyszer";
        var revivalPosition = FindResurrectionPosition(corpse);
        if (revivalPosition is null) return "a tetem körül nincs szabad hely a visszatéréshez";

        var parameters = SpellExecutionService.ParseEffectParameters(effect.Parameter);
        var manaPercent = parameters.Count > 0 && int.TryParse(parameters[0], out var parsedMana)
            ? Math.Clamp(parsedMana, 0, 100)
            : 0;
        foreach (var statusId in parameters.Skip(1)) corpse.Character.RemoveStatus(statusId);
        corpse.Character.ClearTemporarySpellEffects();
        corpse.Character.SetCurrentResources(
            Math.Max(1, corpse.Character.MaximumVitality * Math.Clamp(effect.Value, 1, 100) / 100),
            corpse.Character.MaximumMana * manaPercent / 100);
        corpse.Character.MarkResurrectedThisLevel();
        _maze.RemoveCorpse(corpse);
        var avatar = new PartyMemberAvatar(revivalPosition.Value, corpse.Character);
        _maze.AddPartyMember(avatar);
        ScheduleNextPartyMove(avatar, DateTime.UtcNow);
        RevealFor(avatar.Character, avatar.Position);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        return $"✨ {corpse.Character.Name} visszatért {corpse.Character.CurrentVitality} HP-val" +
               (corpse.Character.UsesMana ? $" és {corpse.Character.CurrentMana} mannával" : string.Empty);
    }

    private Position? FindResurrectionPosition(PartyMemberCorpse corpse) =>
        SpellExecutionService.FindResurrectionPosition(_maze, _player?.Position, corpse);

    private void ApplyExplorationSpellDamage(LiveCharacter caster, Enemy enemy, int amount, List<string> notes)
    {
        enemy.ReceiveSpellDamage(amount);
        if (enemy.CurrentHitPoints > 0) return;
        PlaySessionSound(SoundEffect.MonsterKilledBySpell);
        RegisterNpcQuestKill(enemy);
        caster.RecordMonsterKill(enemy.Definition.Id);
        _maze.ReplaceEnemyWithCorpse(enemy);
        _nextEnemyMoves.Remove(enemy);
        var awards = DistributeExperience(caster, enemy.Definition.ExperienceReward);
        notes.Add($"☠ {enemy.Name} elpusztult; {FormatExperienceAwards(awards)}");
        _renderer.DrawMapCellAfterBattle(_maze, _fogOfWar, enemy.Position, _player.Position);
        var leveledAwards = awards.Where(award => award.Result.LeveledUp && award.Character.IsAlive).ToList();
        if (leveledAwards.Count == 0) return;
        if (_battleStarted)
            _pendingLevelUps.AddRange(leveledAwards.Select(award => (award.Character, award.Result)));
        else
        {
            foreach (var award in leveledAwards) ResolvePerkOffers(award.Character, award.Result);
            _renderer.RefreshCharacterSheet(SelectedCharacter);
        }
    }

    private bool TeleportLeader(Position target, bool inCombat)
    {
        if (!_maze.IsWalkable(target) || _maze.GetObjectAt(target) is not null) return false;
        _player.TeleportTo(target);
        _leaderTrail.Clear();
        _leaderTrail.Add(target);
        RevealFor(SelectedCharacter, target);
        if (!inCombat) _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, target);
        return true;
    }

    private string TeleportLivingParty(Position target, bool inCombat)
    {
        if (!TeleportLeader(target, inCombat)) return "a dimenziókapu célmezője nem szabad";
        var positions = SpellExecutionService.FindNearbyTeleportPositions(_maze, _player?.Position, target).Take(_maze.PartyMembers.Count).ToList();
        var moved = 0;
        foreach (var pair in _maze.PartyMembers.Zip(positions))
        {
            pair.First.MoveTo(pair.Second);
            RevealFor(pair.First.Character, pair.Second);
            moved++;
        }
        if (!inCombat) _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, target);
        return $"dimenziókapu: a vezér és {moved} társ átkerült";
    }

    private Position? SelectSpellTarget(LiveCharacter caster, Position casterPosition, SpellDefinition spell, Enemy? currentEnemy)
    {
        if (spell.TargetType is SpellTargetType.Self or SpellTargetType.Party) return casterPosition;
        var candidates = GetValidSpellTargets(casterPosition, spell, currentEnemy).Distinct().ToList();
        var forward = DirectionOffset(_leaderFacing);
        var fallback = new Position(
            Math.Clamp(casterPosition.X + forward.X, 0, _maze.Width - 1),
            Math.Clamp(casterPosition.Y + forward.Y, 0, _maze.Height - 1));
        var cursor = candidates.OrderBy(position => Chebyshev(position, casterPosition)).FirstOrDefault(fallback);
        Position? previous = null;

        while (true)
        {
            var valid = IsValidSpellTarget(casterPosition, spell, cursor, currentEnemy);
            var prompt = $"╳ {spell.Name} — {ConsoleRenderer.SpellTargetName(spell.TargetType)}, táv {spell.Range}" +
                         (spell.AreaRadius > 0 ? $", sugár {spell.AreaRadius}" : string.Empty) +
                         $" | {(valid ? DescribeSpellTarget(caster, spell, cursor, currentEnemy) : "érvénytelen cél")} | Enter: célzás, Tab: következő, Esc: mégse";
            _renderer.DrawSpellTargetCursor(_maze, _fogOfWar, previous, cursor, valid, prompt);
            previous = cursor;
            var key = Console.ReadKey(intercept: true);
            if (IsHelpShortcut(key))
            {
                ShowInGameHelp();
                if (currentEnemy is not null) _renderer.DrawBattleStarted(currentEnemy);
                previous = null;
                continue;
            }
            if (key.Key == ConsoleKey.Escape)
            {
                _renderer.FinishSpellTargeting(_maze, _fogOfWar, _player.Position);
                return null;
            }
            if (key.Key == ConsoleKey.Enter && valid)
            {
                _renderer.FinishSpellTargeting(_maze, _fogOfWar, _player.Position);
                return cursor;
            }
            if (key.Key == ConsoleKey.Tab && candidates.Count > 0)
            {
                var index = candidates.IndexOf(cursor);
                cursor = candidates[(index + 1 + candidates.Count) % candidates.Count];
                continue;
            }
            if (!TryGetDirection(key.Key, out var direction)) continue;
            cursor = spell.TargetType == SpellTargetType.Direction
                ? casterPosition + direction
                : cursor + direction;
            if (!_maze.IsInside(cursor)) cursor = previous.Value;
        }
    }

    private IEnumerable<Position> GetValidSpellTargets(Position casterPosition, SpellDefinition spell, Enemy? currentEnemy) =>
        _spellExecutionService.GetValidSpellTargets(casterPosition, spell, currentEnemy, _maze, _fogOfWar, _player?.Position, SelectedCharacter);

    private bool IsValidSpellTarget(Position casterPosition, SpellDefinition spell, Position position, Enemy? currentEnemy) =>
        _spellExecutionService.IsValidSpellTarget(casterPosition, spell, position, currentEnemy, _maze, _fogOfWar, _player?.Position, SelectedCharacter);

    private bool HasValidSpellTarget(LiveCharacter caster, Position casterPosition, SpellDefinition spell, Enemy? currentEnemy) =>
        _spellExecutionService.HasValidSpellTarget(caster, casterPosition, spell, currentEnemy, LivingPartyWithPositions().ToArray(), _maze, _fogOfWar, _player?.Position, SelectedCharacter);

    private bool CanAffectCharacter(SpellDefinition spell, LiveCharacter character) =>
        _spellExecutionService.CanAffectCharacter(spell, character);

    private string DescribeSpellTarget(LiveCharacter caster, SpellDefinition spell, Position position, Enemy? currentEnemy) =>
        _spellExecutionService.DescribeSpellTarget(caster, spell, position, currentEnemy, _maze, _player?.Position, SelectedCharacter);

    private static string DirectionName(Position origin, Position position) => position.X < origin.X ? "bal" :
        position.X > origin.X ? "jobb" : position.Y < origin.Y ? "fel" : "le";

    private static int Chebyshev(Position first, Position second) =>
        Math.Max(Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));

    private IReadOnlyList<Position> RevealFor(LiveCharacter character, Position position,
        bool advanceEnemyMemory = false)
    {
        var exitWasRevealed = _fogOfWar.IsRevealed(_maze.Exit);
        var sources = LivingPartyWithPositions().Select(entry => new PartyPerceptionSource(entry.Position,
            CharacterClassRules.VisionRange(entry.Character, CurrentLevelVisionModifier),
            CharacterClassRules.HearingRange(entry.Character),
            CharacterClassRules.DetectionBonus(entry.Character))).ToArray();
        var revealed = _fogOfWar.UpdatePartyVisibility(_maze, sources, advanceEnemyMemory);
        if (_fogOfWar.IsRevealed(_maze.Exit))
        {
            _backgroundMusic.MarkExitDiscovered();
            if (!exitWasRevealed) RegisterNpcQuestProgress(NpcQuestType.Explore, "EXIT");
        }
        return revealed;
    }

    private int CurrentLevelVisionModifier => _locationKind == AdventureLocationKind.Quest
        ? QuestLocationConfigurations.Get(_locationId).VisionModifier
        : MazeLevelConfigurations.Get(_mazeLevel).VisionModifier;

    private void CheckBossDiscoveryAt(IEnumerable<Position> positions, LiveCharacter discoverer)
    {
        var revealed = positions.ToHashSet();
        if (revealed.Count > 0)
        {
            var newlySpottedChests = _maze.TreasureChests
                .Where(chest => revealed.Contains(chest.Position) && _spottedChestIds.Add(chest.Id)).ToArray();
            if (newlySpottedChests.Length > 0)
                TryLogPartyComments(PartySituationIds.TreasureChestFound);
        }
        CheckBossDiscovery(_maze.Enemies, discoverer);
    }

    private void CarryPersistentTemporaryFollowers()
    {
        _temporaryFollowersEnteringNextMaze.Clear();
        foreach (var avatar in _maze.PartyMembers.Where(member => member.TemporaryFollower is { } follower &&
                     string.Equals(follower.StoryId, RodericStoryId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            _temporaryFollowersEnteringNextMaze.Add(avatar.TemporaryFollower!);
            _maze.RemovePartyMember(avatar);
            _nextPartyMoves.Remove(avatar);
        }
    }

    private void PlaceCarriedTemporaryFollowersNear(Position leaderPosition)
    {
        if (_temporaryFollowersEnteringNextMaze.Count == 0) return;
        var positions = FindNearbyFreePositions(leaderPosition).Take(_temporaryFollowersEnteringNextMaze.Count).ToArray();
        for (var index = 0; index < Math.Min(positions.Length, _temporaryFollowersEnteringNextMaze.Count); index++)
        {
            var follower = _temporaryFollowersEnteringNextMaze[index];
            follower.MoveTo(positions[index]);
            var avatar = new PartyMemberAvatar(positions[index], follower.Character, follower);
            _maze.AddPartyMember(avatar);
            _nextPartyMoves[avatar] = DateTime.UtcNow;
        }
        _temporaryFollowersEnteringNextMaze.Clear();
    }

    private void CheckBossDiscovery(IEnumerable<Enemy> enemies, LiveCharacter? discoverer = null)
    {
        var visibleEnemies = enemies.Where(enemy => _fogOfWar.IsEnemyVisible(enemy.Id, enemy.Position))
            .DistinctBy(enemy => enemy.Id).ToList();
        var newlySpotted = visibleEnemies.Where(enemy => _spottedEnemyIds.Add(enemy.Id)).ToList();
        if (newlySpotted.Count > 0)
        {
            PlaySessionSound(SoundEffect.MonsterSpotted);
            TryReportEnemyAlertness(discoverer, newlySpotted);
            TryLogPartyComments(PartySituationIds.EnemySpotted);
        }
        var discovered = visibleEnemies.Where(enemy =>
                (enemy.Definition.IsBoss || enemy.Definition.Rank == EnemyRank.MiniBoss) &&
                !_seenBossIds.Contains(enemy.Definition.Id))
            .DistinctBy(enemy => enemy.Definition.Id, StringComparer.OrdinalIgnoreCase).ToList();
        if (discovered.Count == 0) return;
        foreach (var boss in discovered)
        {
            _seenBossIds.Add(boss.Definition.Id);
            if (string.Equals(boss.Definition.Id, MonsterIds.SirMalrec, StringComparison.OrdinalIgnoreCase) &&
                FindRodericFollower() is { } roderic &&
                string.Equals(roderic.StoryStateId, "MALREC_APPROACH", StringComparison.OrdinalIgnoreCase))
            {
                StageRodericForMalrecEncounter(boss, roderic);
                RunStoryConversation(roderic);
                _renderer.DrawInitialState(_maze, _player, _fogOfWar, _difficultyLevel);
                continue;
            }
            var narrative = StoryNarratives.BossNarratives.GetValueOrDefault(boss.Definition.Id)
                ?? new BossNarrative("Ismeretlen fejezet",
                    [$"Én vagyok {boss.Name}. E folyosók titkait nem osztom meg veletek."]);
            var isMiniBoss = boss.Definition.Rank == EnemyRank.MiniBoss;
            ShowSynchronizedNarrative(NarrativeKind.BossIntroduction,
                isMiniBoss ? "MINIBOSS KÖZELEG" : "BOSS KÖZELEG",
                narrative.ChapterTitle, narrative.Speech,
                new BossPresentationSnapshot(boss.Name, boss.Definition.Appearance,
                    boss.Definition.StrengthTier, isMiniBoss ? "⚔ Nincs aranykulcs" : "🔑 Aranykulcs"));
        }
    }

    private void StageRodericForMalrecEncounter(Enemy malrec, WorldNpc roderic)
    {
        var avatar = _maze.PartyMembers.FirstOrDefault(member => member.TemporaryFollower == roderic);
        if (avatar is null || Manhattan(avatar.Position, malrec.Position) <= 3) return;
        var candidates = FindNearbyFreePositions(malrec.Position)
            .Where(position => _maze.GetTrapAt(position) is null && _maze.GetDoorAt(position) is null)
            .Take(24)
            .ToArray();
        var destination = candidates
            .Where(position => Manhattan(position, malrec.Position) is >= 2 and <= 3)
            .OrderBy(position => Manhattan(position, _player.Position))
            .Select(position => (Position?)position)
            .FirstOrDefault();
        destination ??= candidates.Select(position => (Position?)position).FirstOrDefault();
        if (destination is null) return;
        avatar.MoveTo(destination.Value);
        _nextPartyMoves[avatar] = DateTime.UtcNow;
    }

    private void TryReportEnemyAlertness(LiveCharacter? discoverer, IReadOnlyCollection<Enemy> newlySpotted)
    {
        if (discoverer is null) return;
        var chance = Math.Clamp(50 + CharacterClassRules.DetectionBonus(discoverer) * 10, 0, 100);
        if (_random.Next(1, 101) > chance) return;

        var observations = newlySpotted
            .GroupBy(enemy => (enemy.Name, enemy.Alertness))
            .Select(group => $"{(group.Count() > 1 ? $"{group.Count()}× " : string.Empty)}{group.Key.Name} — " +
                             EnemyAlertnessName(group.Key.Alertness));
        _sessionEventService.LogPartyComment(discoverer,
            $"Felderítés: {string.Join("; ", observations)}.");
    }

    private static string EnemyAlertnessName(EnemyAlertness alertness) => alertness switch
    {
        EnemyAlertness.Sleeping => "alszik",
        EnemyAlertness.Drowsy => "álmos",
        _ => "éber"
    };

    #region Combat

    private void AwardBossKey(Enemy enemy)
    {
        if (!enemy.Definition.IsBoss || !_collectedBossKeyIds.Add(enemy.Definition.Id)) return;
        _renderer.SetGoldenKeyCount(_collectedBossKeyIds.Count);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        var completed = _collectedBossKeyIds.Count == MonsterIds.Bosses.Count
            ? " A tizenkét aranykulcs összegyűlt — a küldetés első célja teljesült!"
            : string.Empty;
        _renderer.DrawInventoryMessage($"🔑 Aranykulcs megszerezve: {enemy.Name}. " +
            $"Kulcsok: {_collectedBossKeyIds.Count}/{MonsterIds.Bosses.Count}.{completed}", ConsoleColor.Yellow);
        if (_collectedBossKeyIds.Count == MonsterIds.Bosses.Count)
            ShowSynchronizedNarrative(NarrativeKind.TwelveKeys, "A TIZENKÉT ZÁR FELNYÍLIK",
                "XIV. fejezet — A Rubin Útja", StoryNarratives.TwelveKeysStory);
    }

    private void StartBattle(Enemy enemy, bool enemyStrikesFirst = false)
        => StartTeamBattle(SelectedCharacter, enemy, enemyStrikesFirst);

    private void StartBattle(PartyMemberAvatar member, Enemy enemy, bool enemyStrikesFirst = false)
        => StartTeamBattle(member.Character, enemy, enemyStrikesFirst);

    private void StartTeamBattle(LiveCharacter initiatingCharacter, Enemy initiatingEnemy, bool enemyStrikesFirst)
    {
        if (_battleStarted || !initiatingCharacter.IsAlive || initiatingEnemy.CurrentHitPoints <= 0) return;
        CheckBossDiscovery([initiatingEnemy], initiatingCharacter);
        _timeStopUsedThisBattle = false;
        _turnUndeadUsedThisBattle.Clear();
        _battleNoPathReported.Clear();
        _battleLogCycle = -1;
        _pendingLevelUps.Clear();
        if (_renderer.IsSpellInfoPageOpen) _renderer.CloseSpellInfoPage();

        var characterParticipants = new List<TeamCharacterParticipant>();
        var preparationEntries = new List<BattleLogEntry>();
        foreach (var (character, position) in LivingPartyWithPositions().DistinctBy(entry => entry.Character.Id))
        {
            var preparation = _battleSystem.PrepareTeamCharacter(character);
            preparationEntries.AddRange(preparation.Entries);
            var avatar = _maze.PartyMembers.FirstOrDefault(member => member.Character == character);
            var kind = avatar?.IsTemporaryFollower == true
                ? TacticalParticipantKind.Follower
                : TacticalParticipantKind.PartyMember;
            characterParticipants.Add(new TeamCharacterParticipant(character, position, kind,
                preparation.Initiative, CharacterMobilityRules.Evaluate(character).CombatMovementAllowance,
                character == initiatingCharacter ? 1 : 2, preparation.Runtime));
        }

        var friendlyPositions = characterParticipants.Select(value => value.Position).ToArray();
        var enemyParticipants = _maze.Enemies
            .Where(enemy => enemy.CurrentHitPoints > 0 &&
                            TacticalDistance.IsWithin(initiatingEnemy.Position, enemy.Position) &&
                            (enemy == initiatingEnemy || CanEnemyReachBattleWithinCycles(enemy, friendlyPositions)))
            .DistinctBy(enemy => enemy.Id)
            .Select(enemy => new TeamEnemyParticipant(enemy, _battleSystem.RollTeamEnemyInitiative(enemy),
                EnemyMovementAllowance(enemy), enemy == initiatingEnemy ? 1 : 2))
            .ToList();
        if (enemyParticipants.All(value => value.Enemy != initiatingEnemy))
            enemyParticipants.Add(new TeamEnemyParticipant(initiatingEnemy,
                _battleSystem.RollTeamEnemyInitiative(initiatingEnemy),
                EnemyMovementAllowance(initiatingEnemy), 1));

        var participantEnemies = enemyParticipants.Select(value => value.Enemy).ToArray();
        foreach (var enemy in participantEnemies) _battleSystem.PrepareEnemyForBattle(enemy);
        var quickAssessment = QuickCombatRules.Assess(characterParticipants.Select(value => value.Character),
            participantEnemies.Select(enemy => enemy.Definition),
            hasAvailableReinforcements: HasAvailableTeamReinforcements(participantEnemies),
            hasActiveFormation: _formation.State != PartyFormationState.Disbanded,
            isQuestImportant: participantEnemies.Any(IsQuestImportantEnemy),
            enemyStrikesFirst: enemyStrikesFirst);
        _isQuickTeamBattle = ShouldUseQuickCombat(quickAssessment);
        _quickBattleSuppressedEntryCount = 0;

        _lastBattleActionDetails = null;
        ResetTeamMovement();
        _activeTeamBattle = new TeamBattleEncounter(initiatingEnemy.Position,
            characterParticipants, enemyParticipants, initiatingCharacter.Id, initiatingEnemy.Id,
            enemyStrikesFirst, formation: ActiveBattleFormation());
        var protectionMessages = new List<string>();
        foreach (var protectedParticipant in characterParticipants)
        {
            var knight = TryRollKnightProtector(protectedParticipant.Character);
            if (knight is null) continue;
            _battleSystem.SetTeamKnightProtection(protectedParticipant.Runtime, knight);
            protectionMessages.Add($"🛡️ {knight.Name} védi {protectedParticipant.Character.Name} első találatát.");
        }
        _activeTeamBattle.Turns.StartTurns();
        _preparedTeamBattleTurnId = 0;
        _battleStarted = true;
        _session.SetPhase(GameSessionPhase.Battle);
        PlaySessionSound(SoundEffect.BattleStart);
        _renderer.DrawBattleStarted(initiatingEnemy);
        TryLogPartyComments(PartySituationIds.BattleStarted);
        PresentBattleEntries(preparationEntries);
        foreach (var protectionMessage in protectionMessages)
        {
            _renderer.DrawInventoryMessage(protectionMessage, ConsoleColor.Cyan);
            RecordSessionActivity(SessionActivityKind.Battle, protectionMessage, ConsoleColor.Cyan);
        }
        var queue = string.Join(" → ", _activeTeamBattle.Turns.Participants
            .OrderByDescending(participant => participant.InitiativeBase)
            .Select(participant =>
            {
                var name = _activeTeamBattle.CharacterFor(participant.Id)?.Name ??
                           _activeTeamBattle.EnemyFor(participant.Id)?.Name ?? participant.Id.Value;
                return $"{name} {participant.InitiativeBase}";
            }));
        var openingFirst = enemyStrikesFirst ? initiatingEnemy.Name : initiatingCharacter.Name;
        var openingSecond = enemyStrikesFirst ? initiatingCharacter.Name : initiatingEnemy.Name;
        var startMessage = _isQuickTeamBattle
            ? $"⚡ GYORSHARC — {initiatingEnemy.Name} ellen. A csapatharc automatikusan lefut."
            : $"⚔️ CSAPATHARC — {characterParticipants.Count} baráti és " +
              $"{enemyParticipants.Count} ellenséges résztvevő. " +
              $"Nyitó ütésváltás: {openingFirst} → {openingSecond}. Utána kezdeményezés: {queue}.";
        if (_activeTeamBattle.HasActiveFormation)
            startMessage += " 🛡️ A zárt alakzat első sora elölről védi a hátsó sort.";
        _renderer.DrawInventoryMessage(startMessage, ConsoleColor.Yellow);
        RecordSessionActivity(SessionActivityKind.Battle, startMessage, ConsoleColor.Yellow);
        ContinueTeamBattle();
    }

    private PartyFormationSnapshot? ActiveBattleFormation()
    {
        if (_formation.State != PartyFormationState.Locked) return null;
        var expected = PartyFormationController.Positions(_formation, SelectedCharacter.Id, _player.Position);
        return expected.All(pair => CharacterRoster.Party.Members.FirstOrDefault(character =>
                    character.Id == pair.Key) is not { IsAlive: true } character ||
                GetCasterPosition(character) == pair.Value)
            ? _formation
            : null;
    }

    private void ContinueTeamBattle()
    {
        while (_activeTeamBattle is { } battle)
        {
            SynchronizeTeamBattleDefeats(battle);
            if (!SelectedCharacter.IsAlive)
            {
                FinishTeamBattle(battle, forceDefeat: true);
                return;
            }
            if (battle.IsCompleted)
            {
                FinishTeamBattle(battle);
                return;
            }
            if (battle.InactiveSidesLastCompletedCycle.Count > 0)
            {
                FinishTeamBattleStalemate(battle);
                return;
            }
            var reinforcementsArrived = TryCallTeamBattleReinforcements(battle);
            if (_isQuickTeamBattle && (reinforcementsArrived || battle.ActionNumber >= 200))
            {
                _isQuickTeamBattle = false;
                var reason = reinforcementsArrived
                    ? "Váratlan erősítés érkezett."
                    : "Az automatikus szimuláció nem tudta gyorsan lezárni az ütközetet.";
                var message = $"⚠️ {reason} A harc taktikai módban folytatódik.";
                _renderer.DrawInventoryMessage(message, ConsoleColor.DarkYellow);
                RecordSessionActivity(SessionActivityKind.Battle, message, ConsoleColor.DarkYellow);
            }

            var current = battle.Current;
            if (_battleLogCycle != battle.Turns.Cycle)
            {
                _battleLogCycle = battle.Turns.Cycle;
                _renderer.SetBattleCommandPanelRound(battle.Turns.Cycle);
            }
            UpdateTeamBattleFocus(battle, current);
            if (battle.CurrentCharacter is { } character)
            {
                if (!character.IsAlive)
                {
                    battle.MarkDefeated(character);
                    AdvanceTeamBattleTurn(battle);
                    continue;
                }
                if (_preparedTeamBattleTurnId != battle.Turns.TurnId)
                {
                    _battleSystem.BeginTeamCharacterTurn(character);
                    _preparedTeamBattleTurnId = battle.Turns.TurnId;
                }
                var runtime = battle.RuntimeFor(character);
                if (runtime.RequiresTacticSelection)
                {
                    if (!_isQuickTeamBattle && _session.IsHumanControlled(character.Id))
                    {
                        var enemy = ClosestLivingTeamEnemy(battle, current.Position);
                        var actions = GetTeamAllowedBattleActions(battle, character, enemy);
                        _session.SetBattlePrompt(battle.Id, battle.Turns.TurnId, character.Id, actions);
                        PublishTeamBattlePrompt(character, enemy, actions, battle);
                        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
                        return;
                    }
                    ChooseTeamAiTactic(character, runtime);
                    continue;
                }
                if (!_isQuickTeamBattle && _session.IsHumanControlled(character.Id))
                {
                    var enemy = ClosestLivingTeamEnemy(battle, current.Position);
                    var actions = GetTeamAllowedBattleActions(battle, character, enemy);
                    _session.SetBattlePrompt(battle.Id, battle.Turns.TurnId, character.Id, actions);
                    PublishTeamBattlePrompt(character, enemy, actions, battle);
                    _activeCoopHost?.TryPublish(CreateSessionSnapshot());
                    return;
                }
                ExecuteTeamAiCharacterTurn(battle, character);
                continue;
            }

            if (battle.CurrentEnemy is { CurrentHitPoints: > 0 } enemyActor)
            {
                if (_isQuickTeamBattle || !CanTeamEnemyActMeaningfully(battle, enemyActor))
                {
                    ExecuteTeamEnemyTurn(battle, enemyActor);
                    continue;
                }
                _session.SetBattlePrompt(battle.Id, battle.Turns.TurnId, SelectedCharacter.Id,
                    [BattleActionKind.AdvanceEnemyTurn]);
                _renderer.DrawBattleCommandPanel(BattleCommandPanel.Format(
                    [BattleActionKind.AdvanceEnemyTurn], enemyTurn: true));
                _activeCoopHost?.TryPublish(CreateSessionSnapshot());
                return;
            }

            AdvanceTeamBattleTurn(battle);
        }
    }

#endregion

    private bool TryCallTeamBattleReinforcements(TeamBattleEncounter battle)
    {
        if (!battle.BeginReinforcementCheckForCurrentCycle()) return false;
        var activeGroups = battle.Enemies.Where(enemy => !string.IsNullOrWhiteSpace(enemy.GroupId))
            .Select(enemy => enemy.GroupId!).ToHashSet(StringComparer.Ordinal);
        if (activeGroups.Count == 0) return false;
        var callers = battle.Enemies.Where(enemy => enemy.CurrentHitPoints > 0).ToArray();
        var friendlyPositions = battle.Characters.Where(character => character.IsAlive)
            .Select(GetCasterPosition).ToArray();
        var reinforcements = _maze.Enemies.Where(enemy => enemy.CurrentHitPoints > 0 && !battle.ContainsEnemy(enemy) &&
                enemy.GroupId is { Length: > 0 } groupId && activeGroups.Contains(groupId) &&
                callers.Any(caller => TacticalDistance.IsWithin(caller.Position, enemy.Position,
                    battle.Turns.Radius * 2)) && CanEnemyReachBattleWithinCycles(enemy, friendlyPositions))
            .ToArray();
        if (reinforcements.Length == 0) return false;
        foreach (var enemy in reinforcements)
        {
            _battleSystem.PrepareEnemyForBattle(enemy);
            battle.TryAddEnemy(new TeamEnemyParticipant(enemy, _battleSystem.RollTeamEnemyInitiative(enemy),
                EnemyMovementAllowance(enemy), battle.Turns.Cycle + 1));
        }
        var message = $"📯 Az ellenség erősítést hív: {reinforcements.Length} új harcos " +
                      $"a(z) {battle.Turns.Cycle + 1}. körben kapcsolódik be.";
        _renderer.DrawInventoryMessage(message, ConsoleColor.DarkYellow);
        RecordSessionActivity(SessionActivityKind.Battle, message, ConsoleColor.DarkYellow);
        return true;
    }

    private bool HasAvailableTeamReinforcements(IReadOnlyCollection<Enemy> participants)
    {
        var participantIds = participants.Select(enemy => enemy.Id).ToHashSet();
        var groupIds = participants.Where(enemy => !string.IsNullOrWhiteSpace(enemy.GroupId))
            .Select(enemy => enemy.GroupId!).ToHashSet(StringComparer.Ordinal);
        if (groupIds.Count == 0) return false;
        var friendlyPositions = LivingPartyWithPositions().Select(entry => entry.Position).ToArray();
        return _maze.Enemies.Any(enemy => enemy.CurrentHitPoints > 0 && !participantIds.Contains(enemy.Id) &&
            enemy.GroupId is { Length: > 0 } groupId && groupIds.Contains(groupId) &&
            participants.Any(caller => TacticalDistance.IsWithin(caller.Position, enemy.Position,
                TacticalDistance.DefaultBattleRadius * 2)) &&
            CanEnemyReachBattleWithinCycles(enemy, friendlyPositions));
    }

    private bool CanEnemyReachBattleWithinCycles(Enemy enemy, IReadOnlyCollection<Position> friendlyPositions)
    {
        var movement = EnemyMovementAllowance(enemy);
        return TacticalArrivalRules.CanReachWithin(enemy.Position,
            movement * TacticalArrivalRules.MaximumArrivalCycles,
            position => CanPotentialBattleParticipantEnter(position),
            position => friendlyPositions.Any(target => TacticalDistance.IsMeleeAdjacent(position, target)));
    }

    private bool CanPotentialBattleParticipantEnter(Position position)
    {
        if (!_maze.IsWalkable(position)) return false;
        var occupant = _maze.GetObjectAt(position);
        return occupant is null or GroundItemPile or Corpse or Enemy or PartyMemberAvatar ||
               Maze.IsPassableNeutralNpc(occupant);
    }

    private static bool IsQuestImportantEnemy(Enemy enemy) => TacticalTeamBattleCoordinator.IsQuestImportantEnemy(enemy);

    private static int EnemyMovementAllowance(Enemy enemy) =>
        Math.Clamp((enemy.EffectiveSpeed + 1) / 2 +
                   (enemy.Definition.HasTrait(EnemyTraits.Flying) ? 1 : 0), 1, 7);

    private bool ShouldUseQuickCombat(QuickCombatAssessment assessment)
    {
        if (!assessment.IsEligible || _musicSettings.Settings.QuickCombat == QuickCombatMode.Never) return false;
        if (_musicSettings.Settings.QuickCombat == QuickCombatMode.Automatic) return true;

        var injuryPercent = (int)Math.Ceiling(assessment.PredictedInjuryRatio * 100);
        var message = $"⚡ Gyorsharc elérhető — becsült sérülés legfeljebb " +
                      $"{assessment.PredictedVitalityLoss} HP ({injuryPercent}%). " +
                      "I / Enter: gyorsharc, N / Esc: taktikai harc.";
        _renderer.DrawInventoryMessage(message, ConsoleColor.Cyan);
        while (true)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key is ConsoleKey.I or ConsoleKey.Y or ConsoleKey.Enter) return true;
            if (key is ConsoleKey.N or ConsoleKey.Escape) return false;
        }
    }

    private void ExecuteTeamBattleAction(TeamBattleEncounter battle, BattleActionCommand command)
    {
        if (command.BattleId != battle.Id || command.TurnId != battle.Turns.TurnId) return;
        if (battle.CurrentEnemy is { } enemyActor)
        {
            if (command.Action != BattleActionKind.AdvanceEnemyTurn || command.CharacterId != SelectedCharacter.Id)
            {
                RejectTeamBattleAction(command, "Most egy ellenfél következik.");
                return;
            }
            ExecuteTeamEnemyTurn(battle, enemyActor);
            ContinueTeamBattle();
            return;
        }
        if (battle.CurrentCharacter is not { } character || character.Id != command.CharacterId)
        {
            RejectTeamBattleAction(command, "Nem ez a karakter van soron.");
            return;
        }
        var focusEnemy = ClosestLivingTeamEnemy(battle, GetCasterPosition(character));
        var allowed = GetTeamAllowedBattleActions(battle, character, focusEnemy);
        if (!allowed.Contains(command.Action))
        {
            RejectTeamBattleAction(command, "Ez az akció most nem használható.");
            return;
        }
        switch (command.Action)
        {
            case BattleActionKind.SelectTarget:
                var selectedEnemy = command.TargetEnemyId is { } selectedId
                    ? battle.Enemies.FirstOrDefault(enemy => enemy.Id == selectedId)
                    : null;
                if (selectedEnemy is null || !ReachableTeamEnemies(battle, character).Contains(selectedEnemy) ||
                    !battle.TrySelectTarget(selectedEnemy))
                {
                    RejectTeamBattleAction(command, "A választott ellenfél nem elérhető célpont.");
                    return;
                }
                _renderer.DrawInventoryMessage($"Célpont: {selectedEnemy.Name}.", ConsoleColor.Yellow);
                break;
            case BattleActionKind.FighterPrecise:
            case BattleActionKind.FighterPowerful:
            case BattleActionKind.FighterDefensive:
            case BattleActionKind.ThiefAmbush:
            case BattleActionKind.ThiefObserve:
            case BattleActionKind.ThiefPoison:
                var tactic = ToBattleTactic(command.Action);
                if (!battle.RuntimeFor(character).TryChooseTactic(character, tactic))
                {
                    RejectTeamBattleAction(command, "Ez a harci taktika most nem választható.");
                    return;
                }
                var tacticMessage = $"{character.Name} harci taktikája: {BattleTacticName(tactic, character)}.";
                _renderer.DrawInventoryMessage(tacticMessage, ConsoleColor.Cyan);
                RecordSessionActivity(SessionActivityKind.Battle, tacticMessage, ConsoleColor.Cyan);
                break;
            case BattleActionKind.PhysicalAttack:
                var target = command.TargetEnemyId is { } targetId
                    ? battle.Enemies.FirstOrDefault(enemy => enemy.Id == targetId)
                    : battle.SelectedTargetEnemy() ??
                      ReachableTeamEnemies(battle, character).OrderBy(enemy => enemy.CurrentHitPoints).FirstOrDefault();
                if (target is null || target.CurrentHitPoints <= 0 ||
                    !ReachableTeamEnemies(battle, character).Contains(target))
                {
                    RejectTeamBattleAction(command, "A választott ellenfél nincs közelharci távolságban.");
                    return;
                }
                ResolveTeamCharacterAttack(battle, character, target);
                break;
            case BattleActionKind.Move when command.Target is { } destination:
                if (!TryExecuteTeamCharacterMove(battle, character, destination, out var movementError))
                {
                    RejectTeamBattleAction(command, movementError);
                    return;
                }
                break;
            case BattleActionKind.MoveFormation when command.Target is { } formationDestination:
                if (!TryExecuteTeamFormationMove(battle, character, formationDestination,
                        out var formationMovementError))
                {
                    RejectTeamBattleAction(command, formationMovementError);
                    return;
                }
                break;
            case BattleActionKind.SwapWeapon:
                if (!character.TrySwapReserveWeapon())
                {
                    RejectTeamBattleAction(command, "A tartalékfegyver most nem vehető kézbe.");
                    return;
                }
                var weaponSwapStatus = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
                _renderer.RefreshCharacterSheet(SelectedCharacter);
                PresentBattleEntries([new BattleLogEntry($"{character.Name}: fegyvercsere → {character.AttackWeapon?.Name}.{weaponSwapStatus}", BattleLogKind.Information)]);
                AdvanceTeamBattleTurn(battle);
                break;
            case BattleActionKind.SwapToRear:
                if (!TryExecuteSwapToRear(battle, character, out var swapError))
                {
                    RejectTeamBattleAction(command, swapError);
                    return;
                }
                break;
            case BattleActionKind.Pass:
                if (IsTeamMovementInProgress(battle))
                {
                    FinishTeamCharacterMovement(battle, character);
                    break;
                }
                var passStatus = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
                PresentBattleEntries([new BattleLogEntry(
                    $"{character.Name}\t⌛ Kivár.{passStatus}",
                    BattleLogKind.Information)]);
                AdvanceTeamBattleTurn(battle);
                break;
            case BattleActionKind.Retreat:
                ExecuteTeamRetreat(battle, character);
                return;
            case BattleActionKind.UseItem when command.BackpackIndex is { } backpackIndex:
                if (!TryUseTeamBattleItem(battle, character, backpackIndex, out var itemMessage))
                {
                    RejectTeamBattleAction(command, itemMessage);
                    return;
                }
                itemMessage +=
                    _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
                PresentBattleEntries([new BattleLogEntry(itemMessage, BattleLogKind.Information)]);
                AdvanceTeamBattleTurn(battle);
                break;
            case BattleActionKind.TurnUndead:
                var undead = command.TargetEnemyId is { } undeadId
                    ? battle.Enemies.FirstOrDefault(enemy => enemy.Id == undeadId)
                    : AdjacentTeamEnemies(battle, character).FirstOrDefault(CanTarget);
                if (undead is null || !CanTurnUndead(character, undead))
                {
                    RejectTeamBattleAction(command, "Nincs elűzhető élőholt a közelben.");
                    return;
                }
                var turning = ResolveTurnUndead(character, undead);
                battle.RecordAttack(BattleSide.Friendly);
                if (turning.DamageToEnemy > 0) undead.ReceiveSpellDamage(turning.DamageToEnemy);
                var turnMessage = turning.Message +
                    _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
                PresentBattleEntries([new BattleLogEntry(turnMessage, turning.Kind)]);
                if (undead.CurrentHitPoints <= 0) ResolveTeamEnemyDefeat(battle, undead, character);
                AdvanceTeamBattleTurn(battle);
                break;
            case BattleActionKind.CastSpell:
                ExecuteTeamSpellBattleAction(battle, character, command);
                break;
        }
        ContinueTeamBattle();

        bool CanTarget(Enemy candidate) => CanTurnUndead(character, candidate);
    }

    private void ExecuteTeamSpellBattleAction(TeamBattleEncounter battle, LiveCharacter character,
        BattleActionCommand command)
    {
        if (command.SpellId is null || command.Target is null) return;
        var spell = _gameData.Spells.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, command.SpellId, StringComparison.OrdinalIgnoreCase));
        if (spell is null)
        {
            RejectTeamBattleAction(command, "Ismeretlen varázslat.");
            return;
        }
        MagicItemDefinition? castingItem = null;
        if (command.CastingItemSlotIndex is { } slot)
            castingItem = character.MagicItems.ElementAtOrDefault(slot);
        var currentEnemy = command.TargetEnemyId is { } enemyId
            ? battle.Enemies.FirstOrDefault(enemy => enemy.Id == enemyId && enemy.CurrentHitPoints > 0)
            : null;
        currentEnemy ??= battle.Enemies.FirstOrDefault(enemy =>
            enemy.Position == command.Target && enemy.CurrentHitPoints > 0);
        currentEnemy ??= ClosestLivingTeamEnemy(battle, GetCasterPosition(character));
        var attempt = TryCastSpell(character, GetCasterPosition(character), spell, inCombat: true,
            currentEnemy, castingItem, command.CastingItemSlotIndex, command.Target);
        if (attempt is null || !attempt.ConsumesTurn)
        {
            RejectTeamBattleAction(command, attempt?.Message ?? "A varázslat célpontja érvénytelen.");
            return;
        }
        battle.RecordSpellCast(character);
        if (IsOffensiveSpell(spell)) battle.RecordAttack(BattleSide.Friendly);
        if (attempt.DamageToCurrentEnemy > 0) currentEnemy.ReceiveSpellDamage(attempt.DamageToCurrentEnemy);
        battle.GrantExtraActions(attempt.ExtraPlayerActions);
        var message = attempt.Message +
                      _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
        PresentBattleEntries([new BattleLogEntry(message, attempt.Kind, attempt.Details)]);
        SynchronizeTeamBattleDefeats(battle, character);
        AdvanceTeamBattleTurn(battle);
    }

    private bool TryUseTeamBattleItem(TeamBattleEncounter battle, LiveCharacter character,
        int backpackIndex, out string message)
    {
        if (backpackIndex is < 0 or >= LiveCharacter.MaximumBackpackItemCount ||
            character.GetInventoryItem(InventorySlotKind.Backpack, backpackIndex) is not MiscItemDefinition item ||
            item.Effect == ConsumableEffect.None || !battle.CanUseItem(character, item))
        {
            message = "A választott hátizsákhelyen nincs használható tárgy.";
            return false;
        }
        var result = item.Id == MiscItemIds.HerbalTea &&
                     (character.WaterLevel < 100 || character.CurrentVitality < character.MaximumVitality)
            ? UseHerbalTea(character, item.EffectValue)
            : IsInitiativeDrink(item) ? UseInitiativeDrink(character, item)
            : item.Effect switch
            {
                ConsumableEffect.Food when character.FoodLevel < 100 => UseFood(character, item.EffectValue),
                ConsumableEffect.Water when character.WaterLevel < 100 => UseWater(character, item.EffectValue),
                ConsumableEffect.Heal when character.CurrentVitality < character.MaximumVitality => UseHealing(character, item.EffectValue),
                ConsumableEffect.RestoreMana when character.UsesMana && character.CurrentMana < character.MaximumMana => UseManaPotion(character, item.EffectValue),
                ConsumableEffect.CurePoison when character.RemoveStatus(CharacterStatusIds.Poisoned) => "a mérgezés megszűnt",
                ConsumableEffect.CureDisease when character.RemoveStatus(CharacterStatusIds.Diseased) => "a betegség megszűnt",
                ConsumableEffect.StopBleeding when character.RemoveStatus(CharacterStatusIds.Bleeding) => "a vérzés elállt",
                ConsumableEffect.Vision => UseVisionItem(character, item),
                _ => string.Empty
            };
        if (string.IsNullOrEmpty(result))
        {
            message = "A tárgy hatására most nincs szükség vagy nem alkalmazható.";
            return false;
        }
        character.RemoveOneInventoryItem(InventorySlotKind.Backpack, backpackIndex);
        character.SynchronizeNeedStatuses(_gameData.GetStatus(CharacterStatusIds.Hungry),
            _gameData.GetStatus(CharacterStatusIds.Thirsty));
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        message = $"{character.Name} használta: {item.Name} — {result}.";
        PlaySessionSound(item.Effect == ConsumableEffect.Heal ? SoundEffect.DefensiveSpell : SoundEffect.Item,
            [character.Id]);
        return true;
    }

    private void RejectTeamBattleAction(BattleActionCommand command, string message)
    {
        _session.RejectExecutedCommand(command, message);
        _renderer.DrawInventoryMessage(message, ConsoleColor.Red);
        if (_activeTeamBattle is { IsCompleted: false } battle && battle.CurrentCharacter is { } character)
        {
            var enemy = ClosestLivingTeamEnemy(battle, GetCasterPosition(character));
            _session.SetBattlePrompt(battle.Id, battle.Turns.TurnId, character.Id,
                GetTeamAllowedBattleActions(battle, character, enemy));
        }
    }

    private void ChooseTeamAiTactic(LiveCharacter character, TeamCharacterBattleRuntime runtime)
    {
        var choices = character.CharacterClass.Id == CharacterClassIds.Harcos
            ? new[] { BattleTactic.FighterPrecise, BattleTactic.FighterPowerful, BattleTactic.FighterDefensive }
            : new[] { BattleTactic.ThiefAmbush, BattleTactic.ThiefObserve, BattleTactic.ThiefPoison };
        var tactic = choices[_random.Next(choices.Length)];
        runtime.TryChooseTactic(character, tactic);
        var message = $"{character.Name} harci taktikája: {BattleTacticName(tactic, character)}.";
        if (!_isQuickTeamBattle) _renderer.DrawInventoryMessage(message, ConsoleColor.Cyan);
        RecordSessionActivity(SessionActivityKind.Battle, message, ConsoleColor.Cyan);
    }

    private void ExecuteTeamAiCharacterTurn(TeamBattleEncounter battle, LiveCharacter character)
    {
        if (battle.IsFrontRow(character) && character.CurrentVitality * 3 <= character.MaximumVitality &&
            battle.RearPartnerOf(character) is { IsAlive: true } &&
            TryExecuteSwapToRear(battle, character, out _))
            return;
        if (TryExecuteTeamAiSpell(battle, character)) return;
        var reachable = ReachableTeamEnemies(battle, character).FirstOrDefault();
        if (reachable is not null)
        {
            ResolveTeamCharacterAttack(battle, character, reachable);
            return;
        }
        if (battle.IsEngaged(character))
        {
            PresentBattleEntries([new BattleLogEntry($"{character.Name} le van kötve, ezért nem tud mozogni.",
                BattleLogKind.Information)]);
            AdvanceTeamBattleTurn(battle);
            return;
        }
        if (battle.HasActiveFormation && battle.FormationSlotFor(character) is not null)
        {
            var statusText = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
            PresentBattleEntries([new BattleLogEntry(
                $"{character.Name} tartja a helyét az alakzatban.{statusText}", BattleLogKind.Information)]);
            AdvanceTeamBattleTurn(battle);
            return;
        }
        var target = ClosestLivingTeamEnemy(battle, GetCasterPosition(character));
        MoveTeamCharacterToward(battle, character, target.Position);
    }

    private bool TryExecuteTeamAiSpell(TeamBattleEncounter battle, LiveCharacter caster)
    {
        var plan = ChooseTeamAiSpell(battle, caster);
        if (plan is null) return false;
        var attempt = TryCastSpell(caster, GetCasterPosition(caster), plan.Spell, inCombat: true,
            plan.Enemy, explicitTarget: plan.Target);
        if (attempt is not { ConsumesTurn: true }) return false;
        battle.RecordSpellCast(caster);
        if (plan.Offensive)
        {
            battle.RecordAttack(BattleSide.Friendly);
            if (attempt.DamageToCurrentEnemy > 0 && plan.Enemy is not null)
                plan.Enemy.ReceiveSpellDamage(attempt.DamageToCurrentEnemy);
        }
        battle.GrantExtraActions(attempt.ExtraPlayerActions);
        var message = attempt.Message +
                      _battleSystem.FinishTeamCharacterAction(caster, battle.RuntimeFor(caster));
        PresentBattleEntries([new BattleLogEntry(message, attempt.Kind)]);
        SynchronizeTeamBattleDefeats(battle, caster);
        AdvanceTeamBattleTurn(battle);
        return true;
    }

    private NpcTeamSpellPlan? ChooseTeamAiSpell(TeamBattleEncounter battle, LiveCharacter caster)
    {
        if (!caster.IsSpellcaster || !caster.CanCastSpells ||
            !SpellcastingRules.HasRequiredFocus(caster)) return null;
        var casterPosition = GetCasterPosition(caster);
        var spells = caster.MemorizedSpells.Where(spell => spell.CanUseInCombat)
            .OrderBy(spell => SpellcastingRules.EffectiveManaCost(caster, spell))
            .ThenBy(spell => spell.Level).ToArray();
        var allies = battle.Characters.Where(character => character.IsAlive)
            .OrderBy(character => VitalityRatio(character)).ToArray();
        var enemies = OrderedNpcSpellTargets(battle, casterPosition).ToArray();
        var currentEnemy = enemies.FirstOrDefault();

        foreach (var spell in spells)
        {
            var effects = _gameData.GetSpellEffects(spell.Id);
            if (!effects.Any(effect => effect.Type == SpellEffectType.Heal)) continue;
            var wounded = allies.Where(NpcSpellcastingPolicy.NeedsHealing).ToArray();
            if (wounded.Length == 0) break;
            IEnumerable<LiveCharacter> targets = spell.TargetType switch
            {
                SpellTargetType.Self => wounded.Where(character => character == caster),
                SpellTargetType.Party => [wounded[0]],
                SpellTargetType.PartyMember => wounded,
                _ => []
            };
            foreach (var target in targets)
            {
                var targetPosition = spell.TargetType is SpellTargetType.Self or SpellTargetType.Party
                    ? casterPosition : GetCasterPosition(target);
                var emergency = spell.TargetType == SpellTargetType.Party
                    ? wounded.Any(NpcSpellcastingPolicy.IsEmergency)
                    : NpcSpellcastingPolicy.IsEmergency(target);
                var manaCost = SpellcastingRules.EffectiveManaCost(caster, spell);
                if (!NpcSpellcastingPolicy.CanSpendMana(caster, manaCost, emergency) ||
                    ValidateSpellCast(caster, casterPosition, spell, true, currentEnemy,
                        explicitTarget: targetPosition) is not null) continue;
                return new NpcTeamSpellPlan(spell, targetPosition, currentEnemy, Offensive: false);
            }
        }

        var vulnerableAlly = allies.FirstOrDefault();
        var dangerousEnemy = vulnerableAlly is null ? null : enemies.FirstOrDefault(enemy =>
            ShouldUseOffensiveSupportSpell(vulnerableAlly, enemy));
        if (dangerousEnemy is null) return null;

        if (battle.Turns.Cycle <= 2)
            foreach (var spell in spells)
            {
                var effects = _gameData.GetSpellEffects(spell.Id);
                if (effects.Any(effect => effect.Type == SpellEffectType.Heal) ||
                    !effects.Any(effect => NpcSpellcastingPolicy.IsBuffEffect(effect.Type)) ||
                    effects.Any(effect => effect.Type == SpellEffectType.ProtectionFromEvil) &&
                    battle.Enemies.Where(enemy => enemy.CurrentHitPoints > 0).All(enemy => !IsUnholy(enemy.Definition)))
                    continue;
                var manaCost = SpellcastingRules.EffectiveManaCost(caster, spell);
                if (!NpcSpellcastingPolicy.CanSpendMana(caster, manaCost)) continue;
                var targetPosition = ChooseNpcBuffTarget(battle, caster, casterPosition, spell, effects, allies);
                if (targetPosition is null || ValidateSpellCast(caster, casterPosition, spell, true,
                        dangerousEnemy, explicitTarget: targetPosition) is not null) continue;
                return new NpcTeamSpellPlan(spell, targetPosition.Value, dangerousEnemy, Offensive: false);
            }

        foreach (var spell in spells)
        {
            var effects = _gameData.GetSpellEffects(spell.Id);
            if (!NpcSpellcastingPolicy.IsSingleTargetOffensive(spell, effects)) continue;
            var manaCost = SpellcastingRules.EffectiveManaCost(caster, spell);
            if (!NpcSpellcastingPolicy.CanSpendMana(caster, manaCost)) continue;
            foreach (var target in enemies.Where(enemy => ShouldUseOffensiveSupportSpell(vulnerableAlly!, enemy)))
            {
                if (ValidateSpellCast(caster, casterPosition, spell, true, target,
                        explicitTarget: target.Position) is not null) continue;
                return new NpcTeamSpellPlan(spell, target.Position, target, Offensive: true);
            }
        }
        return null;
    }

    private Position? ChooseNpcBuffTarget(TeamBattleEncounter battle, LiveCharacter caster,
        Position casterPosition, SpellDefinition spell, IReadOnlyList<SpellEffectDefinition> effects,
        IReadOnlyList<LiveCharacter> allies) =>
        _teamBattleCoordinator.ChooseNpcBuffTarget(battle, caster, casterPosition, spell, effects, allies,
            GetCasterPosition, (c, pos, sp, tgt, en) => IsValidExplicitSpellTarget(c, pos, sp, tgt, en));

    private IEnumerable<Enemy> OrderedNpcSpellTargets(TeamBattleEncounter battle, Position casterPosition) =>
        TacticalTeamBattleCoordinator.OrderedNpcSpellTargets(battle, casterPosition);

    private static double VitalityRatio(LiveCharacter character) =>
        TacticalTeamBattleCoordinator.VitalityRatio(character);

    private void ResolveTeamCharacterAttack(TeamBattleEncounter battle, LiveCharacter character, Enemy enemy)
    {
        battle.RecordAttack(BattleSide.Friendly);
        if (TacticalDistance.IsMeleeAdjacent(GetCasterPosition(character), enemy.Position))
            battle.Engage(character, enemy);
        var targets = TacticalTeamBattleCoordinator.SweepTargets(battle, character, GetCasterPosition(character), enemy);
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (TacticalDistance.IsMeleeAdjacent(GetCasterPosition(character), target.Position))
                battle.Engage(character, target);
            var entry = _battleSystem.ResolveTeamCharacterAttack(character, battle.RuntimeFor(character), target,
                finishAction: index == targets.Count - 1);
            PresentBattleEntries([entry]);
            if (target.CurrentHitPoints <= 0) ResolveTeamEnemyDefeat(battle, target, character);
        }
        if (!character.IsAlive) ResolveTeamCharacterDefeat(battle, character);
        AdvanceTeamBattleTurn(battle);
    }

    private void ExecuteTeamEnemyTurn(TeamBattleEncounter battle, Enemy enemy)
    {
        var turnStart = _battleSystem.BeginEnemyTurn(enemy);
        if (turnStart.Entries.Count > 0) PresentBattleEntries(turnStart.Entries);
        if (enemy.CurrentHitPoints <= 0)
        {
            ResolveTeamEnemyDefeat(battle, enemy, null);
            AdvanceTeamBattleTurn(battle);
            return;
        }
        if (!turnStart.CanAct)
        {
            AdvanceTeamBattleTurn(battle);
            return;
        }

        var livingTargets = TeamEnemyTargets(battle, enemy)
            .OrderBy(character => TacticalDistance.Between(enemy.Position, GetCasterPosition(character))).ToArray();
        var closestDistance = livingTargets.Length == 0 ? int.MaxValue :
            TacticalDistance.Between(enemy.Position, GetCasterPosition(livingTargets[0]));
        var activeAbility = enemy.PreparedWeaponId is null
            ? _battleSystem.SelectEnemyActiveAbility(enemy, closestDistance)
            : null;
        if (activeAbility is not null)
        {
            var abilityTargets = livingTargets.Where(character =>
                    TacticalDistance.Between(enemy.Position, GetCasterPosition(character)) <= activeAbility.Range)
                .Take(activeAbility.MaximumTargets).ToArray();
            PresentBattleEntries(abilityTargets.Select((target, index) =>
                _battleSystem.ResolveTeamEnemyAbility(enemy, target, battle.RuntimeFor(target), activeAbility,
                    consumeResources: index == 0)).ToArray());
            battle.RecordAttack(BattleSide.Hostile);
            foreach (var target in abilityTargets.Where(target => !target.IsAlive))
                ResolveTeamCharacterDefeat(battle, target);
            AdvanceTeamBattleTurn(battle);
            return;
        }

        var attackWeapon = _battleSystem.SelectEnemyAttackWeapon(enemy, weapon =>
            TacticalTeamBattleCoordinator.EnemyAttackTargets(battle, enemy, weapon, GetCasterPosition).Count);
        var targets = TacticalTeamBattleCoordinator.EnemyAttackTargets(battle, enemy, attackWeapon,
            GetCasterPosition);
        if (targets.Count == 0)
        {
            var target = TeamEnemyTargets(battle, enemy)
                .OrderBy(character => TacticalDistance.Between(enemy.Position, GetCasterPosition(character)))
                .First();
            MoveTeamEnemyToward(battle, enemy, GetCasterPosition(target));
            return;
        }
        if (BattleSystem.IsTelegraphedWeapon(attackWeapon) && !enemy.IsWeaponPrepared(attackWeapon!.Id))
        {
            PresentBattleEntries([_battleSystem.PrepareEnemyWeapon(enemy, attackWeapon)]);
            AdvanceTeamBattleTurn(battle);
            return;
        }

        var entries = new List<BattleLogEntry>();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (TacticalDistance.IsMeleeAdjacent(enemy.Position, GetCasterPosition(target)))
                battle.Engage(target, enemy);
            var entry = _battleSystem.ResolveTeamEnemyAction(enemy, target, battle.RuntimeFor(target),
                attackWeapon, advanceAttackerEffects: index == 0);
            entries.Add(entry);
            if (entry.Kind is BattleLogKind.EnemyAttack or BattleLogKind.CriticalHit)
                battle.RecordAttack(BattleSide.Hostile);
            if (!target.IsAlive) ResolveTeamCharacterDefeat(battle, target);
            if (enemy.CurrentHitPoints <= 0 || entry.Kind == BattleLogKind.Information) break;
        }
        _battleSystem.MarkEnemyWeaponUsed(enemy, attackWeapon);
        PresentBattleEntries(entries);
        if (enemy.CurrentHitPoints <= 0) ResolveTeamEnemyDefeat(battle, enemy, null);
        AdvanceTeamBattleTurn(battle);
    }

    private void AdvanceTeamBattleTurn(TeamBattleEncounter battle)
    {
        ResetTeamMovement();
        battle.CaptureNewStatuses();
        if (battle.IsCompleted)
        {
            battle.RecordCompletedFinalAction();
            return;
        }
        battle.AdvanceTurn();
        _preparedTeamBattleTurnId = 0;
    }

    private void ExecuteTeamRetreat(TeamBattleEncounter battle, LiveCharacter character)
    {
        if (character != SelectedCharacter || battle.Turns.Cycle <= 1)
        {
            _renderer.DrawInventoryMessage("A visszavonulást csak a vezér rendelheti el a nyitó ütésváltás után.",
                ConsoleColor.Red);
            return;
        }

        foreach (var retreatingCharacter in battle.Characters.Where(candidate => candidate.IsAlive))
        {
            var attacker = battle.EngagedEnemies(retreatingCharacter)
                .Where(enemy => TacticalDistance.IsMeleeAdjacent(enemy.Position,
                    GetCasterPosition(retreatingCharacter)))
                .OrderByDescending(enemy => enemy.EffectiveSpeed).FirstOrDefault();
            if (attacker is null) continue;
            var entry = _battleSystem.ResolveTeamOpportunityAttack(attacker, retreatingCharacter,
                battle.RuntimeFor(retreatingCharacter));
            PresentBattleEntries([entry]);
            if (!retreatingCharacter.IsAlive) ResolveTeamCharacterDefeat(battle, retreatingCharacter);
        }
        var statusText = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
        if (!string.IsNullOrEmpty(statusText))
            PresentBattleEntries([new BattleLogEntry($"{character.Name} visszavonulási kísérlete.{statusText}",
                BattleLogKind.Information)]);
        if (!character.IsAlive) ResolveTeamCharacterDefeat(battle, character);
        if (!SelectedCharacter.IsAlive)
        {
            FinishTeamBattle(battle, forceDefeat: true);
            return;
        }

        var friendlySpeed = battle.Characters.Where(candidate => candidate.IsAlive)
            .Select(candidate => CharacterMobilityRules.Evaluate(candidate).CombatMovementAllowance)
            .DefaultIfEmpty(0).Min();
        var hostileSpeed = battle.Enemies.Where(enemy => enemy.CurrentHitPoints > 0)
            .Select(EnemyMovementAllowance).DefaultIfEmpty(0).Max();
        var fastEnough = friendlySpeed > hostileSpeed;
        Dictionary<LiveCharacter, Position> destinations = [];
        var hasSafeRoute = fastEnough && TryFindTeamRetreatDestinations(battle, out destinations);
        if (!hasSafeRoute)
        {
            var failed = fastEnough
                ? "🏃 A visszavonulás nem sikerült: nincs elérhető biztonságos visszavonulási hely."
                : $"🏃 A visszavonulás nem sikerült: a csapat sebessége {friendlySpeed}, " +
                  $"az üldözőké {hostileSpeed}.";
            _renderer.DrawInventoryMessage(failed, ConsoleColor.Red);
            RecordSessionActivity(SessionActivityKind.Battle, failed, ConsoleColor.Red);
            AdvanceTeamBattleTurn(battle);
            ContinueTeamBattle();
            return;
        }

        foreach (var (retreatingCharacter, destination) in destinations)
        {
            if (retreatingCharacter == SelectedCharacter) _player.TeleportTo(destination);
            else if (_maze.PartyMembers.FirstOrDefault(member => member.Character == retreatingCharacter) is { } avatar)
                avatar.MoveTo(destination);
            battle.UpdatePosition(retreatingCharacter, destination);
            battle.MarkRetreated(retreatingCharacter);
            RevealFor(retreatingCharacter, destination);
        }
        FinishSuccessfulTeamRetreat(battle, friendlySpeed, hostileSpeed);
    }

    private bool TryFindTeamRetreatDestinations(TeamBattleEncounter battle,
        out Dictionary<LiveCharacter, Position> destinations)
    {
        destinations = [];
        var occupied = battle.Turns.Participants.Where(participant => participant.Side == BattleSide.Hostile &&
                participant.State is TacticalParticipantState.Active or TacticalParticipantState.Approaching)
            .Select(participant => participant.Position).ToHashSet();
        var friendlyCharacters = battle.Characters.ToHashSet();
        foreach (var character in battle.Characters.Where(candidate => candidate.IsAlive)
                     .OrderByDescending(candidate => candidate == SelectedCharacter))
        {
            var origin = GetCasterPosition(character);
            occupied.Remove(origin);
            var queue = new Queue<(Position Position, int Distance)>();
            var visited = new HashSet<Position> { origin };
            queue.Enqueue((origin, 0));
            Position? destination = null;
            while (queue.Count > 0)
            {
                var (position, distance) = queue.Dequeue();
                if (distance > 0 && battle.Enemies.Where(enemy => enemy.CurrentHitPoints > 0)
                        .All(enemy => TacticalDistance.Between(position, enemy.Position) >= 3))
                {
                    destination = position;
                    break;
                }
                if (distance >= 8) continue;
                foreach (var direction in Directions)
                {
                    var next = position + direction;
                    if (!visited.Add(next) || occupied.Contains(next) || !_maze.IsWalkable(next)) continue;
                    var mapObject = _maze.GetObjectAt(next);
                    if (mapObject is not null and not GroundItemPile and not Corpse &&
                        !(mapObject is PartyMemberAvatar member && friendlyCharacters.Contains(member.Character))) continue;
                    queue.Enqueue((next, distance + 1));
                }
            }
            if (destination is null) return false;
            destinations.Add(character, destination.Value);
            occupied.Add(destination.Value);
        }
        return true;
    }

    private void FinishSuccessfulTeamRetreat(TeamBattleEncounter battle, int friendlySpeed, int hostileSpeed)
    {
        _session.EndBattle(battle.Id);
        foreach (var character in battle.Characters.Where(character => character.IsAlive))
            DrainNeedsAfterTeamBattle(character, battle.Turns.Cycle);
        ResetTeamMovement();
        _activeTeamBattle = null;
        _isQuickTeamBattle = false;
        _preparedTeamBattleTurnId = 0;
        _battleStarted = false;
        _saveAfterBattle = false;
        _session.SetPhase(GameSessionPhase.Exploration);
        _nextNeedsDrain = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        InitializeEnemyMoveSchedule(DateTime.UtcNow + TimeSpan.FromSeconds(2));
        foreach (var member in _maze.PartyMembers) ScheduleNextPartyMove(member, DateTime.UtcNow);
        var message = $"🏃 Sikeres visszavonulás: a csapat sebessége {friendlySpeed}, " +
                      $"az üldözőké {hostileSpeed}.";
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
        RecordSessionActivity(SessionActivityKind.Battle, message, ConsoleColor.Green);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private IReadOnlyList<BattleActionKind> GetTeamAllowedBattleActions(TeamBattleEncounter battle,
        LiveCharacter character, Enemy focusEnemy)
    {
        if (IsTeamMovementInProgress(battle)) return [BattleActionKind.Move, BattleActionKind.Pass];
        return _teamBattleCoordinator.GetTeamAllowedBattleActions(battle, character, focusEnemy, SelectedCharacter,
            GetCasterPosition(character), HasUsableCombatSpell(character, GetCasterPosition(character), focusEnemy),
            _turnUndeadUsedThisBattle);
    }

    private IReadOnlyList<BattleTacticOptionSnapshot>? GetTeamBattleTacticOptions(TeamBattleEncounter battle,
        LiveCharacter character, Enemy enemy) =>
        _teamBattleCoordinator.GetTeamBattleTacticOptions(battle, character, enemy);

    private void PublishTeamBattlePrompt(LiveCharacter character, Enemy enemy,
        IReadOnlyList<BattleActionKind> actions, TeamBattleEncounter battle)
    {
        var message = BattleCommandPanel.Format(actions,
            battle.RuntimeFor(character).RequiresTacticSelection
                ? GetTeamBattleTacticOptions(battle, character, enemy)
                : null);
        _renderer.DrawBattleCommandPanel(message);
    }

    private IEnumerable<Enemy> AdjacentTeamEnemies(TeamBattleEncounter battle, LiveCharacter character) =>
        TacticalTeamBattleCoordinator.AdjacentTeamEnemies(battle, character, GetCasterPosition(character));

    private IEnumerable<Enemy> ReachableTeamEnemies(TeamBattleEncounter battle, LiveCharacter character) =>
        TacticalTeamBattleCoordinator.ReachableTeamEnemies(battle, character, GetCasterPosition(character));

    private IEnumerable<LiveCharacter> AdjacentTeamCharacters(TeamBattleEncounter battle, Enemy enemy) =>
        TacticalTeamBattleCoordinator.AdjacentTeamCharacters(battle, enemy, GetCasterPosition);

    private IEnumerable<LiveCharacter> TeamEnemyTargets(TeamBattleEncounter battle, Enemy enemy) =>
        TacticalTeamBattleCoordinator.TeamEnemyTargets(battle, enemy);

    private static IEnumerable<Position> TeamMeleePositions(Position center) =>
        TacticalTeamBattleCoordinator.TeamMeleePositions(center);

    private CombatantId? TeamBattleFocusTarget(TeamBattleEncounter battle, TacticalBattleParticipant current)
    {
        if (battle.CharacterFor(current.Id) is { } character)
        {
            var enemy = battle.SelectedTargetEnemy() ??
                        ReachableTeamEnemies(battle, character).OrderBy(candidate => candidate.CurrentHitPoints)
                .FirstOrDefault() ?? battle.Enemies.Where(candidate => candidate.CurrentHitPoints > 0)
                .OrderBy(candidate => TacticalDistance.Between(current.Position, candidate.Position)).FirstOrDefault();
            return enemy is null ? null : CombatantId.ForEnemy(enemy.Id);
        }
        if (battle.EnemyFor(current.Id) is not { } actingEnemy) return null;
        var target = AdjacentTeamCharacters(battle, actingEnemy)
            .OrderBy(candidate => (double)candidate.CurrentVitality / Math.Max(1, candidate.MaximumVitality))
            .FirstOrDefault() ?? TeamEnemyTargets(battle, actingEnemy)
            .OrderBy(candidate => TacticalDistance.Between(current.Position, GetCasterPosition(candidate))).FirstOrDefault();
        return target is null ? null : CombatantId.ForCharacter(target.Id);
    }

    private Enemy? NextTeamBattleTarget(TeamBattleEncounter battle, LiveCharacter character) =>
        TacticalTeamBattleCoordinator.NextTeamBattleTarget(battle, character, GetCasterPosition(character));

    private void UpdateTeamBattleFocus(TeamBattleEncounter battle, TacticalBattleParticipant current)
    {
        if (_isQuickTeamBattle) return;
        _renderer.DrawTacticalBattleActor(battle.CharacterFor(current.Id), battle.EnemyFor(current.Id));
        var targetId = TeamBattleFocusTarget(battle, current);
        var targetPosition = targetId is { } id ? battle.Turns.Find(id)?.Position : null;
        _renderer.DrawTeamBattleFocus(_maze, _fogOfWar, _player.Position, current.Position, targetPosition);
    }

    private bool CanTeamEnemyActMeaningfully(TeamBattleEncounter battle, Enemy enemy)
    {
        var possibleWeapons = enemy.Definition.Weapon is { } selected
            ? new[] { selected }
            : enemy.Definition.Weapons ?? [];
        if (possibleWeapons.Any(weapon => TacticalTeamBattleCoordinator.EnemyAttackTargets(
                battle, enemy, weapon, GetCasterPosition).Count > 0)) return true;
        if (battle.IsEngaged(enemy)) return false;
        var target = TeamEnemyTargets(battle, enemy)
            .OrderBy(character => TacticalDistance.Between(enemy.Position, GetCasterPosition(character)))
            .First();
        var goals = TeamMeleePositions(GetCasterPosition(target))
            .Where(position => CanTeamBattleEnter(battle, position, CombatantId.ForEnemy(enemy.Id)))
            .ToArray();
        return FindTeamBattlePath(battle, enemy.Position, goals, CombatantId.ForEnemy(enemy.Id)).Count > 0;
    }

    private Enemy ClosestLivingTeamEnemy(TeamBattleEncounter battle, Position origin) =>
        TacticalTeamBattleCoordinator.ClosestLivingTeamEnemy(battle, origin);

    private void MoveTeamCharacterToward(TeamBattleEncounter battle, LiveCharacter character, Position target)
    {
        var goals = TeamMeleePositions(target)
            .Where(position => CanTeamBattleEnter(battle, position, CombatantId.ForCharacter(character.Id)))
            .ToArray();
        var path = FindTeamBattlePath(battle, GetCasterPosition(character), goals,
            CombatantId.ForCharacter(character.Id));
        CompleteTeamCharacterMovement(battle, character, path);
    }

    private void MoveTeamEnemyToward(TeamBattleEncounter battle, Enemy enemy, Position target)
    {
        var goals = TeamMeleePositions(target)
            .Where(position => CanTeamBattleEnter(battle, position, CombatantId.ForEnemy(enemy.Id)))
            .ToArray();
        var path = FindTeamBattlePath(battle, enemy.Position, goals, CombatantId.ForEnemy(enemy.Id));
        var traversed = path.Take(battle.Current.MovementAllowance).ToArray();
        var landingIndex = Array.FindLastIndex(traversed, position =>
            CanTeamBattleEnter(battle, position, CombatantId.ForEnemy(enemy.Id)));
        var steps = landingIndex < 0 ? Array.Empty<Position>() : traversed.Take(landingIndex + 1).ToArray();
        var previousPosition = enemy.Position;
        if (steps.Length > 0)
        {
            battle.RecordMovement(BattleSide.Hostile);
            enemy.MoveTo(steps[^1]);
            battle.UpdatePosition(enemy);
            if (!_isQuickTeamBattle)
                _renderer.DrawEnemyMovement(_maze, _fogOfWar, previousPosition, enemy.Position, _player.Position);
        }
        if (steps.Length > 0)
            PresentBattleEntries([new BattleLogEntry($"{enemy.Name} {steps.Length} mezőt közeledik.",
                BattleLogKind.Information)]);
        AdvanceTeamBattleTurn(battle);
    }

    private bool TryExecuteTeamFormationMove(TeamBattleEncounter battle, LiveCharacter character,
        Position target, out string error)
    {
        if (character != SelectedCharacter || !battle.HasActiveFormation)
        {
            error = "Az alakzatot csak a vezér mozgathatja.";
            return false;
        }
        var origin = GetCasterPosition(character);
        var deltaX = target.X - origin.X;
        var deltaY = target.Y - origin.Y;
        if (Math.Abs(deltaX) + Math.Abs(deltaY) != 1)
        {
            error = "Az alakzat egy akcióval pontosan egy mezőt mozoghat.";
            return false;
        }
        var direction = deltaX switch
        {
            < 0 => Direction.Left,
            > 0 => Direction.Right,
            _ => deltaY < 0 ? Direction.Up : Direction.Down
        };
        var destinations = battle.FormationDestinations(direction);
        if (destinations.Count == 0)
        {
            error = "Nincs mozgatható harci alakzat.";
            return false;
        }
        if (!battle.PreservesEngagements(destinations))
        {
            error = "Az alakzat lépése szétszakítaná a fennálló lekötést.";
            return false;
        }
        var movingIds = destinations.Keys.Select(value => CombatantId.ForCharacter(value.Id)).ToHashSet();
        var movingAvatars = destinations.Keys.Select(value =>
                _maze.PartyMembers.FirstOrDefault(member => member.Character == value))
            .Where(member => member is not null).ToHashSet();
        foreach (var destination in destinations.Values)
        {
            if (!_maze.IsWalkable(destination) || _maze.GetEnemyAt(destination) is not null ||
                battle.Turns.Participants.Any(participant => !movingIds.Contains(participant.Id) &&
                    participant.State is TacticalParticipantState.Active or TacticalParticipantState.Approaching &&
                    participant.Position == destination))
            {
                error = "Az alakzat egyik célmezője foglalt vagy nem járható.";
                return false;
            }
            var occupant = _maze.GetObjectAt(destination);
            if (occupant is null or GroundItemPile or Corpse || Maze.IsPassableNeutralNpc(occupant) ||
                occupant is PartyMemberAvatar avatar && movingAvatars.Contains(avatar)) continue;
            error = "Az alakzat egyik célmezőjét tereptárgy vagy másik lény foglalja el.";
            return false;
        }

        var previous = destinations.Keys.ToDictionary(value => value, GetCasterPosition);
        foreach (var (member, destination) in destinations)
        {
            if (member == SelectedCharacter) _player.TeleportTo(destination);
            else _maze.PartyMembers.First(avatar => avatar.Character == member).MoveTo(destination);
        }
        battle.UpdateFormationPositions(destinations);
        battle.RecordMovement(BattleSide.Friendly);
        foreach (var (member, destination) in destinations)
        {
            var revealed = RevealFor(member, destination);
            if (member == SelectedCharacter)
                _renderer.DrawMovement(_maze, _fogOfWar, previous[member], destination, revealed, hasWon: false);
            else
                _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, previous[member], destination, revealed,
                    _player.Position);
        }
        var statusText = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
        PresentBattleEntries([new BattleLogEntry($"{character.Name} egy mezővel mozgatja az egész alakzatot.{statusText}",
            BattleLogKind.Information)]);
        AdvanceTeamBattleTurn(battle);
        error = string.Empty;
        return true;
    }

    private bool TryExecuteSwapToRear(TeamBattleEncounter battle, LiveCharacter character, out string error)
    {
        if (!battle.TrySwapToRear(character, out var rear, out var frontPosition, out var rearPosition,
                out var transferredEngagements) || rear is null)
        {
            error = "A Hátra! akcióhoz élő társ szükséges közvetlenül a karakter mögött.";
            return false;
        }
        MoveBattleCharacterTo(character, rearPosition);
        MoveBattleCharacterTo(rear, frontPosition);
        battle.RecordMovement(BattleSide.Friendly);
        _formation = battle.Formation!;
        _renderer.SetFormationStatus(_formation);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        var statusText = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
        var transferText = transferredEngagements == 0
            ? string.Empty
            : $" {rear.Name} {transferredEngagements} lekötést átvett.";
        PresentBattleEntries([new BattleLogEntry(
            $"Hátra! {character.Name} helyet cserél {rear.Name} karakterrel.{transferText}{statusText}",
            BattleLogKind.Information)]);
        AdvanceTeamBattleTurn(battle);
        error = string.Empty;
        return true;
    }

    private void MoveBattleCharacterTo(LiveCharacter character, Position position)
    {
        if (character == SelectedCharacter) _player.TeleportTo(position);
        else _maze.PartyMembers.First(member => member.Character == character).MoveTo(position);
    }

    private bool TryExecuteTeamCharacterMove(TeamBattleEncounter battle, LiveCharacter character, Position target,
        out string error)
    {
        if (battle.IsEngaged(character))
        {
            error = $"{character.Name} le van kötve, ezért nem mozoghat.";
            return false;
        }
        BeginTeamMovementIfNeeded(battle);
        var path = FindStraightTeamBattlePath(battle, GetCasterPosition(character), target,
            CombatantId.ForCharacter(character.Id), maximumSteps: 1);
        if (path.Count == 0)
        {
            error = "Ebben az irányban nincs szabad, elérhető mező. Az akciód megmaradt.";
            return false;
        }
        CompleteTeamCharacterMovement(battle, character, path, finishAction: false);
        _teamMovementRemaining--;
        _teamMovementSteps++;
        if (_teamMovementRemaining <= 0) FinishTeamCharacterMovement(battle, character);
        error = string.Empty;
        return true;
    }

    private IReadOnlyList<Position> FindStraightTeamBattlePath(TeamBattleEncounter battle, Position origin,
        Position target, CombatantId actorId, int maximumSteps)
    {
        var deltaX = target.X - origin.X;
        var deltaY = target.Y - origin.Y;
        if ((deltaX == 0) == (deltaY == 0)) return [];
        var stepX = Math.Sign(deltaX);
        var stepY = Math.Sign(deltaY);
        var requestedSteps = Math.Abs(deltaX != 0 ? deltaX : deltaY);
        var steps = new List<Position>();
        var current = origin;
        for (var index = 0; index < Math.Min(maximumSteps, requestedSteps); index++)
        {
            var next = new Position(current.X + stepX, current.Y + stepY);
            if (!CanTeamBattleEnter(battle, next, actorId)) break;
            steps.Add(next);
            current = next;
        }
        return steps;
    }

    private void CompleteTeamCharacterMovement(TeamBattleEncounter battle, LiveCharacter character,
        IReadOnlyList<Position> path, bool finishAction = true)
    {
        var steps = path.Take(battle.Current.MovementAllowance).ToArray();
        if (steps.Length > 0)
        {
            _battleNoPathReported.Remove(character.Id);
            battle.RecordMovement(BattleSide.Friendly);
            var previousPosition = GetCasterPosition(character);
            var destination = steps[^1];
            if (character == SelectedCharacter) _player.TeleportTo(destination);
            else _maze.PartyMembers.First(member => member.Character == character).MoveTo(destination);
            battle.UpdatePosition(character, destination);
            var newlyRevealed = RevealFor(character, destination);
            if (!_isQuickTeamBattle)
            {
                if (character == SelectedCharacter)
                    _renderer.DrawMovement(_maze, _fogOfWar, previousPosition, destination, newlyRevealed, hasWon: false);
                else
                    _renderer.DrawPartyMemberMovement(_maze, _fogOfWar, previousPosition, destination, newlyRevealed,
                        _player.Position);
            }
        }
        if (!finishAction) return;
        var statusText = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
        var message = steps.Length > 0
            ? $"{character.Name}\t👣 {steps.Length} mezőt mozog{statusText}"
            : $"{character.Name}\t⛔ Nincs járható út{statusText}";
        if (steps.Length > 0 || _battleNoPathReported.Add(character.Id) || !string.IsNullOrEmpty(statusText))
            PresentBattleEntries([new BattleLogEntry(message, BattleLogKind.Information)]);
        AdvanceTeamBattleTurn(battle);
    }

    private bool IsTeamMovementInProgress(TeamBattleEncounter battle) =>
        _teamMovementTurnId == battle.Turns.TurnId && _teamMovementSteps > 0;

    private void BeginTeamMovementIfNeeded(TeamBattleEncounter battle)
    {
        if (_teamMovementTurnId == battle.Turns.TurnId) return;
        _teamMovementTurnId = battle.Turns.TurnId;
        _teamMovementRemaining = battle.Current.MovementAllowance;
        _teamMovementSteps = 0;
    }

    private void FinishTeamCharacterMovement(TeamBattleEncounter battle, LiveCharacter character)
    {
        var statusText = _battleSystem.FinishTeamCharacterAction(character, battle.RuntimeFor(character));
        PresentBattleEntries([new BattleLogEntry(
            $"{character.Name}\t👣 {_teamMovementSteps} mezőt mozog{statusText}", BattleLogKind.Information)]);
        ResetTeamMovement();
        AdvanceTeamBattleTurn(battle);
    }

    private void ResetTeamMovement()
    {
        _teamMovementTurnId = -1;
        _teamMovementRemaining = 0;
        _teamMovementSteps = 0;
    }

    private IReadOnlyList<Position> FindTeamBattlePath(TeamBattleEncounter battle, Position origin,
        IReadOnlyCollection<Position> goals, CombatantId actorId)
    {
        if (goals.Count == 0) return [];
        var goalSet = goals.ToHashSet();
        var queue = new Queue<Position>();
        var previous = new Dictionary<Position, Position> { [origin] = origin };
        queue.Enqueue(origin);
        Position? found = goalSet.Contains(origin) ? origin : null;
        while (queue.Count > 0 && found is null)
        {
            var current = queue.Dequeue();
            foreach (var direction in Directions)
            {
                var next = current + direction;
                if (previous.ContainsKey(next) ||
                    !CanTeamBattleEnter(battle, next, actorId) && !CanFlyingEnemyTraverse(battle, next, actorId)) continue;
                previous[next] = current;
                if (goalSet.Contains(next)) { found = next; break; }
                queue.Enqueue(next);
            }
        }
        if (found is null || found == origin) return [];
        var path = new List<Position>();
        for (var current = found.Value; current != origin; current = previous[current]) path.Add(current);
        path.Reverse();
        return path;
    }

    private bool CanTeamBattleEnter(TeamBattleEncounter battle, Position position, CombatantId actorId)
    {
        if (!_maze.IsWalkable(position)) return false;
        if (battle.Turns.Participants.Any(participant => participant.Id != actorId &&
                participant.State is TacticalParticipantState.Active or TacticalParticipantState.Approaching &&
                participant.Position == position)) return false;
        var occupant = _maze.GetObjectAt(position);
        var actorCharacter = battle.CharacterFor(actorId);
        var actorEnemy = battle.EnemyFor(actorId);
        return occupant is null or GroundItemPile or Corpse || occupant == actorEnemy ||
               occupant is PartyMemberAvatar member && member.Character == actorCharacter ||
               Maze.IsPassableNeutralNpc(occupant);
    }

    private bool CanFlyingEnemyTraverse(TeamBattleEncounter battle, Position position, CombatantId actorId)
    {
        var enemy = battle.EnemyFor(actorId);
        if (enemy is null || !enemy.Definition.HasTrait(EnemyTraits.Flying) || !_maze.IsWalkable(position))
            return false;
        return battle.Turns.Participants.Any(participant => participant.Id != actorId &&
            participant.State is TacticalParticipantState.Active or TacticalParticipantState.Approaching &&
            participant.Position == position);
    }

    private void SynchronizeTeamBattleDefeats(TeamBattleEncounter battle, LiveCharacter? killer = null)
    {
        foreach (var enemy in battle.Enemies.Where(enemy => enemy.CurrentHitPoints <= 0).ToArray())
            ResolveTeamEnemyDefeat(battle, enemy, killer);
        foreach (var character in battle.Characters.Where(character => !character.IsAlive).ToArray())
            ResolveTeamCharacterDefeat(battle, character);
    }

    private void ResolveTeamEnemyDefeat(TeamBattleEncounter battle, Enemy enemy, LiveCharacter? killer)
    {
        if (!battle.TryResolveDeath(enemy)) return;
        battle.MarkDefeated(enemy);
        if (!_maze.Enemies.Contains(enemy)) return;
        AwardBossKey(enemy);
        RegisterNpcQuestKill(enemy);
        var credited = killer ?? battle.Characters.FirstOrDefault(character => character.IsAlive);
        if (credited is not null)
        {
            credited.RecordMonsterKill(enemy.Definition.Id);
            var awards = DistributeExperience(credited, enemy.Definition.ExperienceReward);
            battle.RecordKill(credited, enemy, awards.Sum(award => award.Result.GainedExperience));
            _pendingLevelUps.AddRange(awards.Where(award => award.Result.LeveledUp && award.Character.IsAlive)
                .Select(award => (award.Character, award.Result)));
        }
        _maze.ReplaceEnemyWithCorpse(enemy);
        _nextEnemyMoves.Remove(enemy);
        var message = $"☠ {enemy.Name} elesett. +{enemy.Definition.ExperienceReward} XP kerül szétosztásra.";
        if (!_isQuickTeamBattle) _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
        RecordSessionActivity(SessionActivityKind.Battle, message, ConsoleColor.Green);
    }

    private void ResolveTeamCharacterDefeat(TeamBattleEncounter battle, LiveCharacter character)
    {
        var avatar = _maze.PartyMembers.FirstOrDefault(member => member.Character == character);
        if (avatar is not null && IsQuestCriticalRoderic(avatar))
        {
            character.RestoreVitality(Math.Max(1, character.MaximumVitality / 3));
            var message = $"{character.Name} eszméletét veszti, de az Ezüst Eskü erejével ismét talpra áll.";
            _renderer.DrawInventoryMessage(message, ConsoleColor.DarkYellow);
            return;
        }
        if (!battle.TryResolveDeath(character)) return;
        battle.MarkDefeated(character);
        if (avatar is not null)
        {
            _maze.ReplacePartyMemberWithCorpse(avatar);
            _nextPartyMoves.Remove(avatar);
        }
        if (character != SelectedCharacter)
        {
            _activeCoopHost?.TryPublishCharacterState(character.Id,
                _gameSaveService.SerializeCharacter(character), CharacterSyncReason.CharacterDied);
            _session.ReleaseCharacterControl(character.Id);
        }
        var messageText = $"☠ {character.Name} elesett a csapatharcban.";
        _renderer.DrawInventoryMessage(messageText, ConsoleColor.Red);
        RecordSessionActivity(SessionActivityKind.Battle, messageText, ConsoleColor.Red);
        PlaySessionSound(SoundEffect.MemberKilled);
        TryLogPartyComments(PartySituationIds.PartyMemberDied);
    }

    private void FinishTeamBattleStalemate(TeamBattleEncounter battle)
    {
        _renderer.DrawBattleCommandPanel(string.Empty);
        _session.EndBattle(battle.Id);
        foreach (var character in battle.Characters.Where(character => character.IsAlive))
            DrainNeedsAfterTeamBattle(character, battle.Turns.Cycle);
        var inactive = battle.InactiveSidesLastCompletedCycle;
        var sideName = inactive.Contains(BattleSide.Friendly) ? "a csapat" : "az ellenséges oldal";
        ResetTeamMovement();
        _activeTeamBattle = null;
        _isQuickTeamBattle = false;
        _preparedTeamBattleTurnId = 0;
        _battleStarted = false;
        _session.SetPhase(GameSessionPhase.Exploration);
        var message = inactive.Count == 2
            ? $"⚖️ Az összecsapás véget ér: egyik oldal sem mozdult vagy támadott " +
              $"{TeamBattleEncounter.InactiveCycleLimit} teljes körön át."
            : $"⚖️ Az összecsapás véget ér: {sideName} nem mozdult és nem támadott " +
              $"{TeamBattleEncounter.InactiveCycleLimit} teljes körön át.";
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        _renderer.DrawInventoryMessage(message, ConsoleColor.DarkYellow);
        RecordSessionActivity(SessionActivityKind.Battle, message, ConsoleColor.DarkYellow);
        foreach (var (character, result) in _pendingLevelUps.ToArray())
            ResolvePerkOffers(character, result);
        _renderer.DrawInitialState(_maze, _player, _fogOfWar, _mazeLevel);
        _pendingLevelUps.Clear();
        if (_saveAfterBattle)
        {
            _saveAfterBattle = false;
            SaveGame();
        }
        InitializeEnemyMoveSchedule(DateTime.UtcNow + TimeSpan.FromSeconds(2));
        foreach (var member in _maze.PartyMembers) ScheduleNextPartyMove(member, DateTime.UtcNow);
        _nextNeedsDrain = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private void FinishTeamBattle(TeamBattleEncounter battle, bool forceDefeat = false)
    {
        _renderer.DrawBattleCommandPanel(string.Empty);
        _session.EndBattle(battle.Id);
        var victory = !forceDefeat && battle.HostileSideDefeated && !battle.FriendlySideDefeated;
        var wasQuickBattle = _isQuickTeamBattle;
        var cycles = Math.Max(1, battle.Turns.Cycle);
        var characterResults = battle.Characters.Select(battle.ResultFor).ToArray();
        var resourceSummary = ConsoleRenderer.FormatTeamBattleResourceSummary(characterResults, cycles);
        ResetTeamMovement();
        _activeTeamBattle = null;
        _isQuickTeamBattle = false;
        _preparedTeamBattleTurnId = 0;
        _battleStarted = false;
        if (!victory)
        {
            _saveAfterBattle = false;
            _renderer.DrawGameOver(SelectedCharacter.Name);
            _gameOver = true;
            _session.SetPhase(GameSessionPhase.GameOver);
            return;
        }

        PlayBattleVictorySound();
        foreach (var character in battle.Characters.Where(character => character.IsAlive))
        {
            DrainNeedsAfterTeamBattle(character, cycles);
            if (character != SelectedCharacter) TryNpcUseConsumables(character);
        }
        var message = ConsoleRenderer.FormatTeamBattleVictorySummary(wasQuickBattle, cycles,
            battle.ActionNumber, battle.Kills);
        _renderer.DrawInventoryMessage(message, ConsoleColor.Green);
        RecordSessionActivity(SessionActivityKind.Battle, message, ConsoleColor.Green);
        var details = $"Eredmény: {resourceSummary}";
        _renderer.DrawInventoryMessage(details, ConsoleColor.Cyan);
        RecordSessionActivity(SessionActivityKind.Battle, details, ConsoleColor.Cyan);
        TryLogPartyComments(PartySituationIds.BattleWon);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        foreach (var (character, result) in _pendingLevelUps.ToArray())
            ResolvePerkOffers(character, result);
        _renderer.RestoreAfterBattle();
        _pendingLevelUps.Clear();
        if (_saveAfterBattle)
        {
            _saveAfterBattle = false;
            SaveGame();
        }
        InitializeEnemyMoveSchedule(DateTime.UtcNow);
        foreach (var member in _maze.PartyMembers) ScheduleNextPartyMove(member, DateTime.UtcNow);
        _session.SetPhase(GameSessionPhase.Exploration);
        _nextNeedsDrain = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private void SetHelpVisibility(PlayerId playerId, CharacterId characterId, bool isOpen)
    {
        var characterName = CharacterRoster.Party.Members
            .FirstOrDefault(character => character.Id == characterId)?.Name ?? "Egy játékos";
        if (isOpen)
        {
            if (!_helpPausePlayers.Add(playerId)) return;
            _helpPauseStartedUtc ??= DateTime.UtcNow;
            var message = $"⏸ {characterName} megnyitotta a súgót. A közös játék szünetel.";
            RecordSessionActivity(SessionActivityKind.System, message, ConsoleColor.Yellow);
            if (playerId != _session.HostPlayerId)
                _renderer.DrawInventoryMessage(message, ConsoleColor.Yellow);
            return;
        }

        if (!_helpPausePlayers.Remove(playerId)) return;
        var resumedMessage = $"▶ {characterName} bezárta a súgót.";
        RecordSessionActivity(SessionActivityKind.System, resumedMessage, ConsoleColor.Green);
        if (playerId != _session.HostPlayerId)
            _renderer.DrawInventoryMessage(resumedMessage, ConsoleColor.Green);
        if (_helpPausePlayers.Count > 0 || _helpPauseStartedUtc is not { } pauseStarted) return;

        var pauseDuration = DateTime.UtcNow - pauseStarted;
        _helpPauseStartedUtc = null;
        _nextNeedsDrain += pauseDuration;
        foreach (var characterIdKey in _nextEnemyMoves.Keys.ToArray())
            _nextEnemyMoves[characterIdKey] += pauseDuration;
    }

    private void DrainNeeds()
    {
        var followers = _maze.PartyMembers.Where(member => member.IsTemporaryFollower)
            .Select(member => member.Character);
        var characters = CharacterRoster.Party.Members.Concat(followers).Distinct();
        _sustenanceService.DrainNeeds(characters, IsAutonomousNpc, LogNewZeroNeed, TryNpcUseConsumables);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
    }

    private int DrainNeedsAfterBattle(LiveCharacter character, int monsterTier) =>
        _sustenanceService.DrainNeedsAfterBattle(character, monsterTier, IsAutonomousNpc, LogNewZeroNeed);

    private void DrainNeedsAfterTeamBattle(LiveCharacter character, int cycles) =>
        _sustenanceService.DrainNeedsAfterTeamBattle(character, cycles, IsAutonomousNpc, LogNewZeroNeed);

    private bool IsAutonomousNpc(LiveCharacter character) =>
        character != SelectedCharacter && !_session.IsHumanControlled(character.Id) &&
        CharacterRoster.Party.Members.Contains(character) &&
        _maze.PartyMembers.Any(member => member.Character == character);

    private void TryNpcUseConsumables(LiveCharacter character)
    {
        if (!character.IsAlive || !IsAutonomousNpc(character)) return;
        if (character.HasStatus(CharacterStatusIds.Hungry))
            TryNpcConsumeNeedItems(character, ConsumableEffect.Food, NpcComplaintKind.Hunger);
        else ClearNpcShortage(character, NpcComplaintKind.Hunger);
        if (character.HasStatus(CharacterStatusIds.Thirsty))
            TryNpcConsumeNeedItems(character, ConsumableEffect.Water, NpcComplaintKind.Thirst);
        else ClearNpcShortage(character, NpcComplaintKind.Thirst);
        if (character.CurrentVitality < character.MaximumVitality)
            TryNpcConsumeHealingPotions(character);
        if (character.CurrentVitality * 2 >= character.MaximumVitality || HasHealingPotion(character))
            ClearNpcShortage(character, NpcComplaintKind.Injured);
        character.SynchronizeNeedStatuses(_gameData.GetStatus(CharacterStatusIds.Hungry),
            _gameData.GetStatus(CharacterStatusIds.Thirsty));
    }

    private void TryNpcConsumeNeedItems(LiveCharacter character, ConsumableEffect effect, NpcComplaintKind kind)
    {
        var desiredServings = _random.Next(1, 4);
        var consumed = new List<string>();
        for (var serving = 0; serving < desiredServings; serving++)
        {
            var current = effect == ConsumableEffect.Food ? character.FoodLevel : character.WaterLevel;
            if (current >= 100) break;
            var candidates = BackpackConsumables(character, effect)
                .Where(entry => Math.Max(0, current + entry.Item.EffectValue - 100) <= 15)
                .ToArray();
            if (candidates.Length == 0) break;
            var selected = candidates[_random.Next(candidates.Length)];
            if (!character.RemoveOneInventoryItem(InventorySlotKind.Backpack, selected.Index)) break;
            if (effect == ConsumableEffect.Food) character.RestoreFood(selected.Item.EffectValue);
            else if (string.Equals(selected.Item.Id, MiscItemIds.HerbalTea, StringComparison.OrdinalIgnoreCase))
                UseHerbalTea(character, selected.Item.EffectValue);
            else if (IsInitiativeDrink(selected.Item)) UseInitiativeDrink(character, selected.Item);
            else character.RestoreWater(selected.Item.EffectValue);
            consumed.Add(selected.Item.Name);
        }
        if (consumed.Count > 0)
        {
            ClearNpcShortage(character, kind);
            var level = effect == ConsumableEffect.Food ? character.FoodLevel : character.WaterLevel;
            var action = effect == ConsumableEffect.Food ? "evett" : "ivott";
            LogNpcAutomation(character, $"{character.Name} {action}: {string.Join(", ", consumed)}. " +
                $"{(effect == ConsumableEffect.Food ? "🍖" : "💧")} {level}/100.", ConsoleColor.Cyan);
            return;
        }
        RegisterNpcShortage(character, kind);
    }

    private void TryNpcConsumeHealingPotions(LiveCharacter character)
    {
        var desiredServings = _random.Next(1, 4);
        var consumed = new List<string>();
        for (var serving = 0; serving < desiredServings; serving++)
        {
            var missingVitality = character.MaximumVitality - character.CurrentVitality;
            if (missingVitality <= 0) break;
            var candidates = BackpackConsumables(character, ConsumableEffect.Heal)
                .Where(entry => Math.Max(0, character.PreviewVitalityRecovery(entry.Item.EffectValue) - missingVitality) <= 15)
                .ToArray();
            if (candidates.Length == 0) break;
            var selected = candidates[_random.Next(candidates.Length)];
            if (!character.RemoveOneInventoryItem(InventorySlotKind.Backpack, selected.Index)) break;
            character.RestoreVitality(selected.Item.EffectValue);
            consumed.Add(selected.Item.Name);
        }
        if (consumed.Count > 0)
        {
            ClearNpcShortage(character, NpcComplaintKind.Injured);
            LogNpcAutomation(character, $"{character.Name} gyógyitalt ivott: {string.Join(", ", consumed)}. " +
                $"❤️ {character.CurrentVitality}/{character.MaximumVitality}.", ConsoleColor.Green);
        }
        else if (character.CurrentVitality * 2 < character.MaximumVitality && !HasHealingPotion(character))
            RegisterNpcShortage(character, NpcComplaintKind.Injured);
    }

    private static IEnumerable<(int Index, MiscItemDefinition Item)> BackpackConsumables(
        LiveCharacter character, ConsumableEffect effect) =>
        PartySustenanceService.BackpackConsumables(character, effect);

    private static bool HasHealingPotion(LiveCharacter character) =>
        PartySustenanceService.HasHealingPotion(character);

    private void LogNewZeroNeed(LiveCharacter character, NpcComplaintKind kind, int previous, int current)
    {
        if (previous <= 0 || current > 0) return;
        ScheduleNpcComplaint(character, kind, DateTime.UtcNow);
    }

    private void ProcessNpcComplaints(DateTime now)
    {
        foreach (var character in _maze.PartyMembers.Select(member => member.Character).Distinct()
                     .Where(IsAutonomousNpc))
        {
            ProcessNpcComplaint(character, NpcComplaintKind.Hunger, character.FoodLevel == 0, now);
            ProcessNpcComplaint(character, NpcComplaintKind.Thirst, character.WaterLevel == 0, now);
            ProcessNpcComplaint(character, NpcComplaintKind.Injured,
                character.CurrentVitality * 2 < character.MaximumVitality && !HasHealingPotion(character), now);
        }
    }

    private void ProcessNpcSelfCare(DateTime now)
    {
        foreach (var character in _maze.PartyMembers.Select(member => member.Character).Distinct()
                     .Where(IsAutonomousNpc))
        {
            if (character.IsAlive && character.CurrentVitality < character.MaximumVitality)
                TryNpcConsumeHealingPotions(character);
            if (character.CurrentVitality * 2 >= character.MaximumVitality || HasHealingPotion(character))
                ClearNpcShortage(character, NpcComplaintKind.Injured);
        }
        ProcessNpcComplaints(now);
    }

    private void ProcessNpcComplaint(LiveCharacter character, NpcComplaintKind kind, bool active, DateTime now)
    {
        var key = (character.Id, kind);
        if (!active)
        {
            _nextNpcComplaints.Remove(key);
            return;
        }
        if (!_nextNpcComplaints.TryGetValue(key, out var next))
        {
            ScheduleNpcComplaint(character, kind, now);
            return;
        }
        if (now < next) return;
        LogScheduledPartyComment(character, kind);
        ScheduleNpcComplaint(character, kind, now);
    }

    private void ScheduleNpcComplaint(LiveCharacter character, NpcComplaintKind kind, DateTime from) =>
        _nextNpcComplaints[(character.Id, kind)] = from + TimeSpan.FromSeconds(_random.Next(120, 181));

    private void RegisterNpcShortage(LiveCharacter character, NpcComplaintKind kind)
    {
        if (!_reportedNpcShortages.Add((character.Id, kind))) return;
        ScheduleNpcComplaint(character, kind, DateTime.UtcNow);
    }

    private void ClearNpcShortage(LiveCharacter character, NpcComplaintKind kind)
    {
        _reportedNpcShortages.Remove((character.Id, kind));
        _nextNpcComplaints.Remove((character.Id, kind));
    }

    private void LogNpcAutomation(LiveCharacter character, string message, ConsoleColor color)
    {
        _renderer.DrawInventoryMessage(message, color);
        RecordSessionActivity(SessionActivityKind.System, message, color);
    }

    private void TryLogPartyComments(string situationId)
    {
        foreach (var selection in PartyCommentarySelector.Select(_gameData, situationId,
                     CharacterRoster.Party.Members, _random))
            LogPartyComment(selection.Speaker, selection.Remark.Text);
        var follower = _maze.PartyMembers.FirstOrDefault(member => member.IsTemporaryFollower &&
            member.Character.IsAlive && _gameData.GetTemporaryFollowerRemarks(situationId, member.Character).Count > 0);
        if (follower is null || !PartyCommentarySelector.ShouldComment(_random.Next(100))) return;
        var remarks = _gameData.GetTemporaryFollowerRemarks(situationId, follower.Character);
        LogPartyComment(follower.Character, remarks[_random.Next(remarks.Count)].Text);
    }

    private void LogScheduledPartyComment(LiveCharacter character, NpcComplaintKind kind)
    {
        var situationId = kind switch
        {
            NpcComplaintKind.Hunger => PartySituationIds.Hungry,
            NpcComplaintKind.Thirst => PartySituationIds.Thirsty,
            _ => PartySituationIds.Injured
        };
        PartyCommentSelection? selection;
        if (_maze.PartyMembers.Any(member => member.IsTemporaryFollower && member.Character == character) &&
            _gameData.GetTemporaryFollowerRemarks(situationId, character) is { Count: > 0 } followerRemarks)
            selection = new PartyCommentSelection(character, followerRemarks[_random.Next(followerRemarks.Count)]);
        else selection = PartyCommentarySelector.SelectFor(_gameData, situationId, character, _random);
        if (selection is null) return;
        var level = kind switch
        {
            NpcComplaintKind.Hunger => character.FoodLevel.ToString(),
            NpcComplaintKind.Thirst => character.WaterLevel.ToString(),
            _ => $"{character.CurrentVitality}/{character.MaximumVitality}"
        };
        LogPartyComment(character, selection.Remark.Text, level);
    }

    private void LogPartyComment(LiveCharacter speaker, string comment, string? level = null) =>
        _sessionEventService.LogPartyComment(speaker, comment, level);

    private void PresentBattleEntries(IEnumerable<BattleLogEntry> entries)
    {
        var materialized = entries.ToArray();
        if (_activeTeamBattle is not null && !_isQuickTeamBattle)
        {
            foreach (var entry in materialized)
            {
                _lastBattleActionDetails = entry.Details ?? new BattleActionDetails(Guid.NewGuid(),
                    _activeTeamBattle.CurrentCharacter?.Name ?? _activeTeamBattle.CurrentEnemy?.Name ?? "Akció",
                    "", ["✨ Akció eredménye", "🎲 Kritikus: nem alkalmazható"], [entry.Message]);
                _renderer.DrawBattleDetails(_lastBattleActionDetails);
            }
        }
        _sessionEventService.PresentBattleEntries(
            materialized,
            _isQuickTeamBattle,
            entry => _renderer.DrawBattleRound(entry),
            _ => _renderer.RefreshBattleStatusRows(),
            null,
            SelectedCharacter.Id,
            _ => _quickBattleSuppressedEntryCount++);
    }

    private void RecordSessionActivity(SessionActivityKind kind, string message, ConsoleColor color,
        IReadOnlyCollection<CharacterId>? listeners = null) =>
        _sessionEventService.RecordSessionActivity(kind, message, color, listeners);

    private void PlayCharacterStepSound(LiveCharacter character) =>
        _sessionEventService.PlayCharacterStepSound(character, SelectedCharacter.Id);

    private void PlayBattleVictorySound() =>
        _sessionEventService.PlayBattleVictorySound(SelectedCharacter.Id);

    private void PlaySessionSound(SoundEffect effect, IReadOnlyCollection<CharacterId>? listeners = null) =>
        _sessionEventService.PlaySessionSound(effect, listeners, SelectedCharacter.Id);

    private void ApplyAudioSettings()
    {
        _backgroundMusic.ApplySettings();
        _soundEffects.ApplySettings();
    }

    private void RecordSessionSound(SoundEffect effect, IReadOnlyList<CharacterId>? listenerCharacterIds) =>
        _sessionEventService.RecordSessionSound(effect, listenerCharacterIds);

    private static ConsoleColor BattleEntryColor(BattleLogKind kind) => SessionEventService.BattleEntryColor(kind);

    private void TeleportLeaderNearExit()
    {
        Position? destination = Directions
            .Select(direction => _maze.Exit + direction)
            .Where(position => _maze.IsWalkable(position) && _maze.GetObjectAt(position) is null)
            .OrderBy(position => Manhattan(position, _player.Position))
            .Select(position => (Position?)position)
            .FirstOrDefault();
        if (destination is null)
        {
            _renderer.DrawDeveloperMessage("Fejlesztői mód: nincs üres járható mező a kijárat mellett.");
            return;
        }

        _player.TeleportTo(destination.Value);
        _leaderTrail.Clear();
        _leaderTrail.Add(destination.Value);
        RevealFor(SelectedCharacter, destination.Value);
        // A fejlesztői teleport közvetlenül is jelzi a kijárat elérését; ne függjön
        // attól, hogy az általános látómező-frissítés új cellának számította-e a kijáratot.
        _backgroundMusic.MarkExitDiscovered();
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, destination.Value);
        _renderer.DrawDeveloperMessage("Fejlesztői mód: a partyvezér a kijárat mellé teleportált.");
    }

    private void TeleportLeaderToNextUniqueNpc()
    {
        var targets = _gameData.NpcEncounters
            .Where(encounter => _gameData.GetNpc(encounter.NpcId).Unique)
            .GroupBy(encounter => encounter.NpcId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(encounter => encounter.MazeLevel).First())
            .OrderBy(encounter => encounter.MazeLevel)
            .ThenBy(encounter => encounter.NpcId, StringComparer.OrdinalIgnoreCase)
            .Select(encounter => new DeveloperUniqueNpcTarget(_gameData.GetNpc(encounter.NpcId),
                encounter.MazeLevel))
            .ToArray();
        if (targets.Length == 0)
        {
            _renderer.DrawDeveloperMessage("Fejlesztői mód: nincs pályához rendelt egyedi NPC.");
            return;
        }

        _lastDeveloperUniqueNpcIndex = (_lastDeveloperUniqueNpcIndex + 1) % targets.Length;
        var target = targets[_lastDeveloperUniqueNpcIndex];
        if (!TryFindUniqueNpcPosition(target.Definition, out var npcPosition))
        {
            RemoveStaleUniqueNpcCharacter(target.Definition);
            _mazeLevel = target.MazeLevel;
            StartNewMaze(showLevelImage: false);
            if (!TryFindUniqueNpcPosition(target.Definition, out npcPosition))
            {
                _renderer.DrawDeveloperMessage($"Fejlesztői mód: {target.Definition.Name} nem helyezhető el a(z) " +
                    $"{target.MazeLevel}. pályán.");
                return;
            }
        }

        Position? destination = Directions
            .Select(direction => npcPosition + direction)
            .Where(IsFreeDeveloperTeleportDestination)
            .OrderBy(position => Manhattan(position, _player.Position))
            .Select(position => (Position?)position)
            .FirstOrDefault();
        destination ??= FindNearbyFreePositions(npcPosition)
            .Where(position => _maze.GetTrapAt(position) is null && _maze.GetDoorAt(position) is null)
            .Select(position => (Position?)position)
            .FirstOrDefault();
        if (destination is null)
        {
            _renderer.DrawDeveloperMessage($"Fejlesztői mód: nincs szabad mező {target.Definition.Name} mellett.");
            return;
        }

        _player.TeleportTo(destination.Value);
        _leaderTrail.Clear();
        _leaderTrail.Add(destination.Value);
        RevealFor(SelectedCharacter, destination.Value);
        RevealFor(SelectedCharacter, npcPosition);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, destination.Value);
        _renderer.DrawDeveloperMessage($"Fejlesztői mód: egyedi NPC " +
            $"{_lastDeveloperUniqueNpcIndex + 1}/{targets.Length} — {target.Definition.Name}, " +
            $"{_mazeLevel}. pálya.");
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
    }

    private bool TryFindUniqueNpcPosition(NpcDefinition definition, out Position position)
    {
        var worldNpc = _maze.WorldNpcs.FirstOrDefault(npc =>
            string.Equals(npc.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase));
        if (worldNpc is not null)
        {
            position = worldNpc.Position;
            return true;
        }

        var avatar = _maze.PartyMembers.FirstOrDefault(member =>
            string.Equals(member.TemporaryFollower?.DefinitionId, definition.Id,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(member.Character.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
        if (avatar is not null)
        {
            position = avatar.Position;
            return true;
        }

        if (string.Equals(SelectedCharacter.Name, definition.Name, StringComparison.OrdinalIgnoreCase))
        {
            position = _player.Position;
            return true;
        }

        position = default;
        return false;
    }

    private void RemoveStaleUniqueNpcCharacter(NpcDefinition definition)
    {
        var partyMembers = CharacterRoster.Party.Members.ToHashSet();
        foreach (var character in CharacterRoster.Characters.Where(character =>
                     !partyMembers.Contains(character) &&
                     string.Equals(character.Name, definition.Name, StringComparison.OrdinalIgnoreCase)).ToArray())
            CharacterRoster.Remove(character);
    }

    private bool IsFreeDeveloperTeleportDestination(Position position) =>
        _maze.IsWalkable(position) && _maze.GetObjectAt(position) is null &&
        _maze.GetTrapAt(position) is null && _maze.GetDoorAt(position) is null;

    private void ToggleDeveloperPhasing()
    {
        _developerPhasing = !_developerPhasing;
        _renderer.DrawDeveloperMessage(_developerPhasing
            ? "Fejlesztői mód: fal-áthaladás engedélyezve."
            : "Fejlesztői mód: fal-áthaladás letiltva.");
    }

    private sealed record HeldInventoryItem(IItemDefinition Item, InventorySlotReference Source, long SourceRevision);
    private sealed record DeveloperUniqueNpcTarget(NpcDefinition Definition, int MazeLevel);
    private sealed record NpcTeamSpellPlan(SpellDefinition Spell, Position Target, Enemy? Enemy, bool Offensive);

    private void FillPartyForDevelopment(IReadOnlyList<string> characterClassIds, string setName)
    {
        if (CharacterRoster.Party.Members.Count >= Party.MaximumSize)
        {
            _renderer.DrawDeveloperMessage("Fejlesztői mód: a parti már teljes (4/4). ");
            return;
        }

        var generator = new RandomCharacterGenerator(_gameData, _random);
        var added = new List<LiveCharacter>();
        foreach (var characterClassId in characterClassIds)
        {
            if (CharacterRoster.Party.Members.Count >= Party.MaximumSize) break;
            var member = generator.CreateDevelopmentCharacter(_gameData.GetCharacterClass(characterClassId),
                CharacterRoster.Characters.Select(character => character.Name).ToList());
            CharacterRoster.Add(member);
            CharacterRoster.Party.Add(member);
            added.Add(member);
        }
        PlacePartyMembersNear(_player.Position);
        foreach (var member in _maze.PartyMembers) RevealFor(member.Character, member.Position);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawDeveloperMessage($"Fejlesztői mód: {setName} osztályszett hozzáadva: " +
            string.Join(", ", added.Select(member => $"{member.Name} ({member.CharacterClass.Name})")) + ".");
    }

    private void AddLevelOnePartyMemberForDevelopment()
    {
        if (CharacterRoster.Party.Members.Count >= Party.MaximumSize)
        {
            _renderer.DrawDeveloperMessage("Fejlesztői mód: a parti már teljes (4/4). ");
            return;
        }

        var generator = new RandomCharacterGenerator(_gameData, _random);
        var member = generator.CreateLevelOne(CharacterRoster.Characters.Select(character => character.Name).ToList());
        CharacterRoster.Add(member);
        CharacterRoster.Party.Add(member);
        PlacePartyMembersNear(_player.Position);
        foreach (var avatar in _maze.PartyMembers) RevealFor(avatar.Character, avatar.Position);
        _renderer.DrawMapVisibilityChanged(_maze, _fogOfWar, _player.Position);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawDeveloperMessage($"Fejlesztői mód: {member.Name} ({member.CharacterClass.Name}) 1. szinten csatlakozott. Profil: {NpcBehaviorName(member.NpcBehavior)}.");
    }

    private void PlacePartyMembersNear(Position origin)
    {
        var alreadyPlaced = _maze.PartyMembers.Select(member => member.Character).ToHashSet();
        var companions = CharacterRoster.Party.Members.Where(member => member != SelectedCharacter && member.IsAlive && !alreadyPlaced.Contains(member)).ToList();
        if (companions.Count == 0) return;

        var positions = FindNearbyFreePositions(origin).Take(companions.Count).ToList();
        for (var index = 0; index < Math.Min(companions.Count, positions.Count); index++)
        {
            if (companions[index].NpcBehavior is null) companions[index].SetNpcBehavior(NpcBehavior.Defensive);
            var avatar = new PartyMemberAvatar(positions[index], companions[index]);
            _maze.AddPartyMember(avatar);
            _nextPartyMoves[avatar] = DateTime.UtcNow + TimeSpan.FromMilliseconds(_random.Next(80, MaximumPartyMoveDelayMilliseconds + 1));
        }
    }

    private IEnumerable<Position> FindNearbyFreePositions(Position origin)
    {
        var yielded = new HashSet<Position>();
        if (_maze.StartingRoom is { } startingRoom && startingRoom.Contains(origin))
        {
            foreach (var position in startingRoom.InteriorPositions()
                         .Where(position => position != origin && _maze.GetObjectAt(position) is null && !IsStartingRoomDoorApproach(startingRoom, position))
                         .OrderByDescending(position => Math.Abs(position.X - origin.X) + Math.Abs(position.Y - origin.Y)))
            {
                yielded.Add(position);
                yield return position;
            }
        }

        var visited = new HashSet<Position> { origin };
        var queue = new Queue<Position>();
        queue.Enqueue(origin);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var direction in Directions)
            {
                var next = current + direction;
                if (!visited.Add(next) || !_maze.IsWalkable(next)) continue;
                queue.Enqueue(next);
                if (!yielded.Contains(next) && next != _maze.Entrance && next != _maze.Exit && next != _player.Position && _maze.GetObjectAt(next) is null)
                {
                    yielded.Add(next);
                    yield return next;
                }
            }
        }
    }

    private bool IsStartingRoomDoorApproach(Room room, Position position)
    {
        var rightBoundary = new Position(room.TopLeft.X + room.Width, position.Y);
        if (position.X == room.TopLeft.X + room.Width - 1 && _maze.IsInside(rightBoundary) &&
            _maze.GetDoorAt(rightBoundary) is not null) return true;

        var bottomBoundary = new Position(position.X, room.TopLeft.Y + room.Height);
        return position.Y == room.TopLeft.Y + room.Height - 1 && _maze.IsInside(bottomBoundary) &&
            _maze.GetDoorAt(bottomBoundary) is not null;
    }

    private LevelUpResult AddExperience(int amount) => SelectedCharacter.AddExperience(
        amount,
        _gameData.ExperienceByLevel,
        _gameData.GetVitalityGrowth(SelectedCharacter.Abilities.Health),
        _gameData.GetManaGrowth(SelectedCharacter.Abilities.Intelligence),
        _gameData.GetCharacterResourceGrowth(SelectedCharacter.CharacterClass.Id),
        _random);

    private IReadOnlyList<ExperienceAward> DistributeExperience(LiveCharacter winner, int totalExperience) =>
        _progressionService.DistributeExperience(winner, totalExperience, CharacterRoster.Party.Members);

    private static readonly HashSet<string> MerchantExcludedItemIds = ["W001", "W005", "A001", "A002",
        // Witcher-only consumables (potions and medical supplies)
        "T011", "T012", "T013", "T014", "T015", "T016", "T017", "T018", "T019", "T020",
        // Secret-stash-only drinks
        "T023", "T024"];


    private IReadOnlyList<IItemDefinition> AllTradableItems() => _gameData.Items.Cast<IItemDefinition>()
        .Concat(_gameData.Weapons).Concat(_gameData.Armors).Concat(_gameData.MagicItems)
        .Where(item => !SpellcastingRules.IsRestrictedFromTradingAndGeneration(item))
        .Where(item => !MerchantExcludedItemIds.Contains(item.Id)).ToList();

    private ExperienceAward AwardExperience(LiveCharacter character, int amount) =>
        _progressionService.AwardExperience(character, amount);

    private LevelUpResult AwardExperienceResult(LiveCharacter character, int amount) =>
        _progressionService.AwardExperienceResult(character, amount);

    private static string FormatExperienceAwards(IEnumerable<ExperienceAward> awards) =>
        CharacterProgressionService.FormatExperienceAwards(awards);

    private void GrantPartyExperienceForDevelopment()
    {
        var awards = CharacterRoster.Party.Members.Where(character => character.IsAlive)
            .Select(character => AwardExperience(character, 5000)).ToList();
        foreach (var award in awards.Where(award => award.Result.LeveledUp))
            ResolvePerkOffers(award.Character, award.Result);
        var weaponGrants = CharacterRoster.Party.Members.Select(character =>
            $"{character.Name}: {DevelopmentWeaponGrantService.Grant(character, _gameData.Weapons, _random).Count}/6 fegyver").ToList();
        _renderer.RefreshCharacterSheet(SelectedCharacter);
        _renderer.DrawDeveloperMessage($"Fejlesztői mód: 5000 XP minden partitagnak. {FormatExperienceAwards(awards)} " +
            string.Join("; ", weaponGrants));
    }

    private void TriggerDeveloperLevelUp()
    {
        var neededExperience = SelectedCharacter.GetExperienceNeededForNextLevel(_gameData.ExperienceByLevel);
        if (neededExperience <= 0)
        {
            _renderer.DrawDeveloperMessage("Fejlesztői mód: a karakter már elérte a maximális szintet.");
            return;
        }

        var result = AddExperience(neededExperience);
        ResolvePerkOffers(SelectedCharacter, result);
        _renderer.RefreshCharacterSheet(SelectedCharacter);
    }

    private void ResolvePerkOffers(LiveCharacter character, LevelUpResult result)
    {
        var offers = CreatePerkOffers(character, result);
        var control = _session.CharacterControls.FirstOrDefault(candidate => candidate.CharacterId == character.Id);
        if (control is { ControllerKind: CharacterControllerKind.RemotePlayer,
                ConnectionState: PlayerConnectionState.Connected, AssignedPlayerId: not null })
        {
            ResolveRemoteLevelUp(character, result, offers);
            return;
        }
        
        PlaySessionSound(SoundEffect.NewSkill, [character.Id]);
        var selectedPerks = _renderer.DrawLevelUpScreen(character, result, offers);
        foreach (var perk in selectedPerks)
            if (character.AddPerk(perk))
            {
                character.ApplyPerkAcquisitionBonus(perk);
            }
        if (ShouldChooseSpecialization(character, offers)) ResolveLocalSpecialization(character);
        ResolveLocalClassFeatureUpgrades(character, result);
        ResolveLocalAbilityIncreases(character, result);
        ResolveLocalWeaponProficiencies(character, result);
        ResolveSpellLearning(character, result);
    }

    private void ResolveRemoteLevelUp(LiveCharacter character, LevelUpResult result,
        IReadOnlyList<PerkOffer> offers)
    {
        WaitForRemoteLevelUpChoice(character, result, LevelUpPromptKind.Summary, [],
            offers.Count > 0
                ? "🌠 Új TEHETSÉG ébred benned! Nyomj meg egy billentyűt... 🌠"
                : "🌟 Nyomj meg egy billentyűt a kaland folytatásához! 🌟");
        foreach (var offer in offers)
        {
            var choices = offer.Choices.Select(perk => new LevelUpChoiceSnapshot(perk.Id, perk.Name, perk.Description)).ToArray();
            PlaySessionSound(SoundEffect.NewSkill, [character.Id]);
            var selectedId = WaitForRemoteLevelUpChoice(character, result, LevelUpPromptKind.PerkChoice, choices,
                $"{offer.Tier}. tehetségfokozat — a nem választott tehetség végleg elveszik.",
                [new($"{character.Name} — {character.CharacterClass.Name} — {offer.Tier}. fokozat", ConsoleColor.Cyan),
                 new($"A tehetség a {offer.TriggerLevel}. szint elérésekor vált elérhetővé.", ConsoleColor.DarkCyan),
                 new("A nem választott tehetség végleg elveszik ennél a karakternél.", ConsoleColor.Red)]);
            var perk = offer.Choices.FirstOrDefault(candidate => candidate.Id == selectedId) ?? offer.Choices[0];
            if (character.AddPerk(perk))
            {
                character.ApplyPerkAcquisitionBonus(perk);
            }
            if (offer.Tier == 1) ResolveRemoteSpecialization(character, result);
        }
        if (ShouldChooseSpecialization(character, offers)) ResolveRemoteSpecialization(character, result);
        ResolveRemoteClassFeatureUpgrades(character, result);
        ResolveRemoteAbilityIncreases(character, result);
        ResolveRemoteWeaponProficiencies(character, result);
        ResolveRemoteSpellLearning(character, result);
    }

    private static bool ShouldChooseSpecialization(LiveCharacter character, IReadOnlyList<PerkOffer> offers) =>
        CharacterProgressionService.ShouldChooseSpecialization(character, offers);

    private void ResolveLocalSpecialization(LiveCharacter character)
    {
        if (character.SpecializationId is not null) return;
        var choices = ClassSpecializations.ForClass(character.CharacterClass.Id);
        if (choices.Count > 0) character.ChooseSpecialization(_renderer.DrawSpecializationChoice(character, choices).Id);
    }

    private void ResolveRemoteSpecialization(LiveCharacter character, LevelUpResult result)
    {
        if (character.SpecializationId is not null) return;
        var choices = ClassSpecializations.ForClass(character.CharacterClass.Id);
        if (choices.Count == 0) return;
        var projected = choices.Select(choice => new LevelUpChoiceSnapshot(choice.Id, choice.Name, choice.Description)).ToArray();
        var selectedId = WaitForRemoteLevelUpChoice(character, result, LevelUpPromptKind.SpecializationChoice,
            projected, "Válassz végleges papi vagy mágusi specializációt.",
            [new($"{character.Name} — {character.CharacterClass.Name}", ConsoleColor.Cyan),
             new("Ez a választás végleges.", ConsoleColor.Red)]);
        character.ChooseSpecialization(choices.FirstOrDefault(choice => choice.Id == selectedId)?.Id ?? choices[0].Id);
    }

    private static IEnumerable<int> PendingClassFeatureMilestones(LiveCharacter character, LevelUpResult result) =>
        CharacterProgressionService.PendingClassFeatureMilestones(character, result);

    private void ResolveLocalClassFeatureUpgrades(LiveCharacter character, LevelUpResult result)
    {
        foreach (var milestone in PendingClassFeatureMilestones(character, result).ToArray())
        {
            var choices = ClassFeatureUpgrades.ForClass(character.CharacterClass.Id)
                .Where(choice => !character.HasClassFeatureUpgrade(choice.Id)).ToArray();
            if (choices.Length == 0) return;
            character.ChooseClassFeatureUpgrade(
                _renderer.DrawClassFeatureUpgradeChoice(character, choices, milestone).Id);
        }
    }

    private void ResolveRemoteClassFeatureUpgrades(LiveCharacter character, LevelUpResult result)
    {
        foreach (var milestone in PendingClassFeatureMilestones(character, result).ToArray())
        {
            var choices = ClassFeatureUpgrades.ForClass(character.CharacterClass.Id)
                .Where(choice => !character.HasClassFeatureUpgrade(choice.Id)).ToArray();
            if (choices.Length == 0) return;
            var projected = choices.Select(choice =>
                new LevelUpChoiceSnapshot(choice.Id, choice.Name, choice.Description)).ToArray();
            var selectedId = WaitForRemoteLevelUpChoice(character, result, LevelUpPromptKind.ClassFeatureChoice,
                projected, $"{milestone}. szint — válassz végleges osztályképesség-fejlesztést.",
                [new($"{character.Name} — {character.CharacterClass.Name} — {milestone}. szint", ConsoleColor.Cyan),
                 new("A választás végleges; a 20. szinten egy másik fejlesztés választható.", ConsoleColor.Red)]);
            character.ChooseClassFeatureUpgrade(choices.FirstOrDefault(choice => choice.Id == selectedId)?.Id ?? choices[0].Id);
        }
    }

    private static IReadOnlyList<(string Id, string Name, string Description)> AbilityIncreaseChoices(
        LiveCharacter character) => CharacterProgressionService.AbilityIncreaseChoices(character);

    private void ResolveLocalAbilityIncreases(LiveCharacter character, LevelUpResult result)
    {
        var earned = result.CurrentLevel / 3;
        while (character.AbilityIncreasesClaimed < earned)
        {
            var choices = AbilityIncreaseChoices(character);
            if (choices.Count == 0) { character.ClaimUnspendableAbilityIncrease(); continue; }
            var milestone = (character.AbilityIncreasesClaimed + 1) * 3;
            ApplyAbilityIncrease(character, _renderer.DrawAbilityIncreaseChoice(character, choices, milestone));
        }
    }

    private void ResolveRemoteAbilityIncreases(LiveCharacter character, LevelUpResult result)
    {
        var earned = result.CurrentLevel / 3;
        while (character.AbilityIncreasesClaimed < earned)
        {
            var choices = AbilityIncreaseChoices(character);
            if (choices.Count == 0) { character.ClaimUnspendableAbilityIncrease(); continue; }
            var milestone = (character.AbilityIncreasesClaimed + 1) * 3;
            var projected = choices.Select(choice =>
                new LevelUpChoiceSnapshot(choice.Id, choice.Name, choice.Description)).ToArray();
            var selectedId = WaitForRemoteLevelUpChoice(character, result, LevelUpPromptKind.AbilityChoice,
                projected, $"{milestone}. szint — növelj meg egy képességet 1 ponttal (maximum 13).",
                [new($"{character.Name} — {milestone}. szint", ConsoleColor.Cyan),
                 new("Növelj meg egy képességet 1 ponttal! Maximum: 13.", ConsoleColor.Green)]);
            ApplyAbilityIncrease(character,
                choices.FirstOrDefault(choice => choice.Id == selectedId).Id ?? choices[0].Id);
        }
    }

    private bool ApplyAbilityIncrease(LiveCharacter character, string abilityId)
    {
        if (!_progressionService.ApplyAbilityIncrease(character, abilityId)) return false;
        PlaySessionSound(SoundEffect.NewSkill, [character.Id]);
        return true;
    }

    private static int EarnedWeaponProficiencyAdvances(LiveCharacter character, int level) =>
        CharacterProgressionService.EarnedWeaponProficiencyAdvances(character, level);

    private IReadOnlyList<(string Id, string Name, string Description)> WeaponProficiencyChoices(
        LiveCharacter character) => _progressionService.WeaponProficiencyChoices(character);

    private static int NextWeaponProficiencyMilestone(LiveCharacter character) =>
        CharacterProgressionService.NextWeaponProficiencyMilestone(character);

    private void ResolveLocalWeaponProficiencies(LiveCharacter character, LevelUpResult result)
    {
        var earned = EarnedWeaponProficiencyAdvances(character, result.CurrentLevel);
        PlaySessionSound(SoundEffect.NewWeaponProficiency, [character.Id]);
        while (character.WeaponProficiencyAdvances < earned)
        {
            var choices = WeaponProficiencyChoices(character);
            if (choices.Count == 0) return;
            var milestone = NextWeaponProficiencyMilestone(character);
            character.TryAdvanceWeaponProficiency(_renderer.DrawWeaponProficiencyChoice(character, choices, milestone));
        }
    }

    private void ResolveRemoteWeaponProficiencies(LiveCharacter character, LevelUpResult result)
    {
        var earned = EarnedWeaponProficiencyAdvances(character, result.CurrentLevel);
        PlaySessionSound(SoundEffect.NewWeaponProficiency, [character.Id]);
        while (character.WeaponProficiencyAdvances < earned)
        {
            var choices = WeaponProficiencyChoices(character);
            if (choices.Count == 0) return;
            var milestone = NextWeaponProficiencyMilestone(character);
            var projected = choices.Select(choice =>
                new LevelUpChoiceSnapshot(choice.Id, choice.Name, choice.Description)).ToArray();
            var selectedId = WaitForRemoteLevelUpChoice(character, result, LevelUpPromptKind.WeaponProficiencyChoice,
                projected, $"{milestone}. szint — válassz fegyverjártassági fejlesztést.",
                [new($"{character.Name} — {(milestone == 1 ? "karakteralkotás" : $"{milestone}. szint")}", ConsoleColor.Cyan),
                 new("Legfeljebb két fegyvercsalád tanulható; egy család Jártas, majd Mester lehet.", ConsoleColor.Green)]);
            character.TryAdvanceWeaponProficiency(choices.FirstOrDefault(choice => choice.Id == selectedId).Id ?? choices[0].Id);
        }
    }

    private void ResolveRemoteSpellLearning(LiveCharacter character, LevelUpResult result)
    {
        if (!character.IsSpellcaster) return;
        var simulatedKnown = character.KnownSpells.Select(spell => spell.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var learningCount = result.Bonuses.Count(bonus =>
        {
            if (!SpellcastingRules.TryGetSchool(character.CharacterClass.Id, out var school)) return false;
            var candidate = _gameData.Spells.FirstOrDefault(spell => spell.School == school &&
                spell.Level <= SpellcastingRules.MaximumSpellLevel(bonus.Level) && !simulatedKnown.Contains(spell.Id));
            if (candidate is null) return false;
            simulatedKnown.Add(candidate.Id);
            return true;
        });
        var learnedNumber = 0;
        foreach (var bonus in result.Bonuses)
        {
            var choices = SpellcastingRules.AvailableUnknownSpells(character, _gameData, bonus.Level);
            if (choices.Count == 0) continue;
            learnedNumber++;
            var projected = choices.Select(spell => new LevelUpChoiceSnapshot(spell.Id,
                $"{spell.Level}. szint — {spell.Name}", spell.Description)).ToArray();
            PlaySessionSound(SoundEffect.NewSpellUnlocked, [character.Id]);
            var selectedId = WaitForRemoteLevelUpChoice(character, result, LevelUpPromptKind.SpellChoice,
                projected, $"{learnedNumber}/{learningCount}. új varázslat");
            character.LearnSpell(choices.FirstOrDefault(spell => spell.Id == selectedId) ?? choices[0]);
        }
    }

    private string? WaitForRemoteLevelUpChoice(LiveCharacter character, LevelUpResult result,
        LevelUpPromptKind kind, IReadOnlyList<LevelUpChoiceSnapshot> choices, string message,
        IReadOnlyList<LevelUpTextLineSnapshot>? contextLines = null)
    {
        var previousPhase = _session.Phase;
        _activeLevelUpPrompt = new LevelUpPromptSnapshot(Guid.NewGuid(), character.Id, character.Name, kind,
            result.PreviousLevel, result.CurrentLevel, result.VitalityGained, result.ManaGained, choices, message,
            result.Bonuses.Select(bonus =>
                new LevelUpBonusSnapshot(bonus.Level, bonus.Vitality, bonus.Mana)).ToArray(), contextLines);
        _levelUpResponse = null;
        _levelUpPromptCompleted = false;
        _session.SetPhase(GameSessionPhase.Paused);
        _renderer.DrawInventoryMessage(
            $"⌛ Várakozás {character.Name} szintlépési döntésére... ⌛", ConsoleColor.Yellow);
        PlaySessionSound(SoundEffect.Waiting, [SelectedCharacter.Id]);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        while (!_levelUpPromptCompleted)
        {
            ProcessSessionCommands();
            var stillConnected = _session.CharacterControls.Any(control => control.CharacterId == character.Id &&
                control.ControllerKind == CharacterControllerKind.RemotePlayer &&
                control.ConnectionState == PlayerConnectionState.Connected);
            if (!stillConnected) break;
            if (_activeCoopHost?.ShouldPublish(DateTime.UtcNow) == true)
                _activeCoopHost.TryPublish(CreateSessionSnapshot());
            Thread.Sleep(20);
        }
        var response = _levelUpResponse;
        _activeLevelUpPrompt = null;
        _levelUpResponse = null;
        _levelUpPromptCompleted = false;
        _session.SetPhase(previousPhase);
        _activeCoopHost?.TryPublish(CreateSessionSnapshot());
        return response;
    }

    private void ResolveSpellLearning(LiveCharacter character, LevelUpResult result)
    {
        if (!character.IsSpellcaster) return;
        var simulatedKnown = character.KnownSpells.Select(spell => spell.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var learningCount = 0;
        foreach (var bonus in result.Bonuses)
        {
            if (!SpellcastingRules.TryGetSchool(character.CharacterClass.Id, out var school)) break;
            var simulatedChoice = _gameData.Spells.FirstOrDefault(spell => spell.School == school &&
                spell.Level <= SpellcastingRules.MaximumSpellLevel(bonus.Level) && !simulatedKnown.Contains(spell.Id));
            if (simulatedChoice is null) continue;
            simulatedKnown.Add(simulatedChoice.Id);
            learningCount++;
        }
        var learnedNumber = 0;
        foreach (var bonus in result.Bonuses)
        {
            var choices = SpellcastingRules.AvailableUnknownSpells(character, _gameData, bonus.Level);
            if (choices.Count > 0)
            {
                learnedNumber++;
                PlaySessionSound(SoundEffect.NewSpellUnlocked, [character.Id]);
                character.LearnSpell(_renderer.DrawSpellLearningScreen(character, choices, learnedNumber, learningCount));
            }
        }
    }

    private IReadOnlyList<PerkOffer> CreatePerkOffers(LiveCharacter character, LevelUpResult result) =>
        _progressionService.CreatePerkOffers(character, result);

}
