using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpokaPilote.Server.Audit;
using SpokaPilote.Server.Rollout;
using SpokaPilote.Server.Security;
using SpokaPilote.Server.Storage;
using static SpokaPilote.Server.Api.GroupEndpoints;

namespace SpokaPilote.Server.Api;

/// <summary>Création ou modification d'un anneau (console).</summary>
public sealed class RingRequest
{
    public string? Name { get; set; }
    public int? DelayDays { get; set; }
    public bool RequireValidation { get; set; }
    public int? MinSuccesses { get; set; }
    public int? MaxFailures { get; set; }
    public string? Description { get; set; }
    public List<long>? Groups { get; set; }
}

public sealed class RingOrderRequest
{
    public List<long>? Ids { get; set; }
}

/// <summary>Motif d'une suspension, commentaire d'une reprise ou d'une approbation.</summary>
public sealed class RolloutActionRequest
{
    public string? Reason { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// Déploiement (4.0) : anneaux, réglages, pilotes en cours de diffusion, suspensions. Consultation : tous les rôles ;
/// suspendre, reprendre, maintenir un blocage : Technicien et Administrateur ; anneaux, réglages et diffusion forcée :
/// Administrateur. Tout est consigné dans le journal d'audit.
/// </summary>
public static class RolloutEndpoints
{
    public const int MaxRings = 20;
    public const int MaxPageSize = 500;

    public static void MapRolloutApi(this RouteGroupBuilder api)
    {
        var view = api.MapGroup("/rollout").RequireAccess(Access.View);
        var operate = api.MapGroup("/rollout").RequireAccess(Access.Operate);
        var admin = api.MapGroup("/rollout").RequireAccess(Access.Admin);

        // Postes 4.0 (jeton d'enrôlement) : décision de diffusion pour CHACUNE de leurs propositions, au lieu de la liste des
        // retenues de tout le parc dans leur politique. Lecture seule (rien n'est enregistré : la première détection reste
        // la réception du premier rapport qui propose une version).
        api.MapPost("/rollout/decide", DecideAsync).RequireAccess(Access.Agent);

        // Compteurs légers (menu, vue d'ensemble) : suspensions à examiner, versions en cours de diffusion.
        view.MapGet("/summary", (RolloutService rollout) =>
        {
            var v = rollout.Current();
            return Results.Ok(new
            {
                enabled = v.Snapshot.Settings.Enabled,
                active = v.Snapshot.RingsActive,
                rings = v.Snapshot.Rings.Count,
                inRollout = v.InRollout,
                suspended = v.Suspended,
                attention = v.Attention,
            });
        });

        view.MapGet("", (ServerStore store, RolloutService rollout) => Results.Ok(Overview(store, rollout)));

        admin.MapPut("/settings", async (HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (req, error) = await ReadAsync<RolloutSettings>(request, ct);
            if (error is not null) return error;
            req!.Breaker ??= new BreakerSettings();
            if (req.Validate() is { } invalid) return BadRequest(invalid);
            if (req.DefaultRingId is { } d && store.GetRings().All(r => r.Id != d)) return BadRequest("Anneau par défaut inconnu.");
            var before = store.GetRolloutSettings();
            store.SaveRolloutSettings(req, clock.GetUtcNow());
            rollout.Changed(urgent: true);
            var detail = $"diffusion {(req.Enabled ? "active" : "désactivée")} · anneau par défaut : {RingLabel(store, req.DefaultRingId)} · " +
                         $"pilotes manquants {(req.ApplyToMissingDrivers ? "soumis" : "non soumis")} aux délais · disjoncteur " +
                         (req.Breaker.Enabled ? $"actif ({req.Breaker.MinFailures} échecs et {req.Breaker.MinFailureRatePercent} % en {req.Breaker.WindowHours} h)" : "désactivé");
            audit.Record(request.HttpContext, AuditActions.RolloutSettings, "Diffusion par anneaux", detail);
            if (before.Enabled != req.Enabled)
                store.AddRolloutEvent(clock.GetUtcNow(), req.Enabled ? "enable" : "disable", Principal.Of(request.HttpContext)!.Actor, detail);
            return Results.Ok(Overview(store, rollout));
        });

        // ------------------------------------------------------------------ anneaux

        admin.MapPost("/rings", async (HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (req, error) = await ReadAsync<RingRequest>(request, ct);
            if (error is not null) return error;
            if (store.GetRings().Count >= MaxRings) return BadRequest($"{MaxRings} anneaux au plus.");
            if (ValidateRing(req!, store, out var input) is { } invalid) return invalid;
            var id = store.CreateRing(input, clock.GetUtcNow());
            if (id is null) return Conflict($"Un anneau « {input.Name} » existe déjà.");
            rollout.Changed(urgent: true);
            audit.Record(request.HttpContext, AuditActions.RolloutRingCreate, input.Name, RingText(input, store));
            return Results.Json(Overview(store, rollout), statusCode: StatusCodes.Status201Created);
        });

        // Anneaux suggérés (Pilote 0 jour, Précoce 3 jours, Général 10 jours), seulement s'il n'y en a encore aucun.
        admin.MapPost("/rings/suggested", (HttpContext http, ServerStore store, RolloutService rollout, AuditService audit, TimeProvider clock) =>
        {
            if (store.GetRings().Count > 0) return Conflict("Des anneaux existent déjà : modifiez-les directement.");
            var now = clock.GetUtcNow();
            foreach (var input in SuggestedRings)
            {
                store.CreateRing(input, now);
                audit.Record(http, AuditActions.RolloutRingCreate, input.Name, RingText(input, store));
            }
            rollout.Changed(urgent: true);
            return Results.Json(Overview(store, rollout), statusCode: StatusCodes.Status201Created);
        });

        admin.MapPut("/rings/order", async (HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit, TimeProvider clock,
            CancellationToken ct) =>
        {
            var (req, error) = await ReadAsync<RingOrderRequest>(request, ct);
            if (error is not null) return error;
            if (req!.Ids is not { Count: > 0 } ids || !store.ReorderRings(ids, clock.GetUtcNow()))
                return BadRequest("Ordre invalide : indiquez tous les anneaux, chacun une fois.");
            rollout.Changed(urgent: true);
            audit.Record(request.HttpContext, AuditActions.RolloutRingOrder, "Anneaux", string.Join(" → ", store.GetRings().Select(r => r.Name)));
            return Results.Ok(Overview(store, rollout));
        });

        admin.MapPut("/rings/{id:long}", async (long id, HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (store.GetRings().FirstOrDefault(r => r.Id == id) is not { } before) return RingNotFound();
            var (req, error) = await ReadAsync<RingRequest>(request, ct);
            if (error is not null) return error;
            if (ValidateRing(req!, store, out var input) is { } invalid) return invalid;
            try
            {
                store.UpdateRing(id, input, clock.GetUtcNow());
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
            rollout.Changed(urgent: true);
            var renamed = before.Name != input.Name ? $"renommé (ancien nom : « {before.Name} ») ; " : "";
            audit.Record(request.HttpContext, AuditActions.RolloutRingUpdate, input.Name, renamed + RingText(input, store));
            return Results.Ok(Overview(store, rollout));
        });

        admin.MapDelete("/rings/{id:long}", (long id, HttpContext http, ServerStore store, RolloutService rollout, AuditService audit) =>
        {
            if (store.GetRings().FirstOrDefault(r => r.Id == id) is not { } ring) return RingNotFound();
            store.DeleteRing(id);
            rollout.Changed(urgent: true);
            audit.Record(http, AuditActions.RolloutRingDelete, ring.Name, "ses postes passent dans l'anneau par défaut");
            return Results.Ok(Overview(store, rollout));
        });

        // ------------------------------------------------------------------ versions

        // Pilotes vus sur le parc : ?status=active (défaut : en cours de diffusion et suspendus) | rollout | suspended |
        // attention | completed | all, ?q= (titre, version, source), ?offset=&limit= (pagination côté serveur).
        view.MapGet("/versions", (string? status, string? q, int? offset, int? limit, RolloutService rollout) =>
        {
            var v = rollout.Current();
            var filter = (status ?? "active").Trim().ToLowerInvariant();
            if (filter is not ("active" or "rollout" or "suspended" or "attention" or "completed" or "all"))
                return BadRequest("Filtre inconnu (active, rollout, suspended, attention, completed, all).");
            var search = q?.Trim();
            // Liste filtrée et triée calculée une fois par état de la diffusion (des dizaines de milliers de versions) ; la recherche
            // et la pagination ne font ensuite que parcourir cette liste.
            IReadOnlyList<VersionPlan> sorted = v.Lists.GetOrAdd(filter, f => v.Plans.Where(p => f switch
            {
                "active" => p.Version.Suspended || (v.Snapshot.RingsActive && p.Status == "rollout"),
                "rollout" => v.Snapshot.RingsActive && p.Status == "rollout",
                "suspended" => p.Version.Suspended,
                "attention" => p.Version.NeedsAttention,
                "completed" => !p.Version.Suspended && (!v.Snapshot.RingsActive || p.Completed),
                _ => true,
            }).OrderByDescending(p => p.Version.NeedsAttention).ThenByDescending(p => p.Version.FirstSeen).ThenByDescending(p => p.Version.Id).ToList());
            IEnumerable<VersionPlan> rows = sorted;
            if (!string.IsNullOrEmpty(search))
                rows = rows.Where(p => p.Version.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                       p.Version.Version.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                       RolloutKeys.SourceLabel(p.Version.Source).Contains(search, StringComparison.OrdinalIgnoreCase));
            var ordered = string.IsNullOrEmpty(search) ? sorted : rows.ToList();
            var skip = Math.Max(0, offset ?? 0);
            var take = Math.Clamp(limit ?? 50, 1, MaxPageSize);
            return Results.Ok(new
            {
                total = ordered.Count,
                offset = skip,
                limit = take,
                rings = v.Snapshot.Rings.Select(r => new { id = r.Id, name = r.Name }),
                items = ordered.Skip(skip).Take(take).Select(p => Row(p, v, rollout.Zone)),
            });
        });

        view.MapGet("/versions/{id:long}", (long id, ServerStore store, RolloutService rollout) =>
        {
            var v = rollout.Current();
            if (!v.ById.TryGetValue(id, out var plan))
            {
                // Version vue après le dernier calcul : recalcul immédiat.
                rollout.Changed(urgent: true);
                v = rollout.Current();
                if (!v.ById.TryGetValue(id, out plan)) return VersionNotFound();
            }
            var rings = v.Snapshot.Rings;
            return Results.Ok(new
            {
                version = Row(plan, v, rollout.Zone),
                unassigned = Stat(v.Snapshot.Stat(id, 0)),
                failures = store.GetRolloutFailures(id).Select(f => new
                {
                    machine = f.Machine, ring = RolloutService.RingName(rings, f.RingId), at = f.At, regression = f.Regression, message = f.Message,
                }),
                history = store.GetRolloutEvents(id, 0, 50).Items.Select(Event),
            });
        });

        operate.MapPost("/versions/{id:long}/suspend", (long id, HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit,
            TimeProvider clock, CancellationToken ct) => VersionAction(request, id, store, rollout, ct, (v, req, actor) =>
        {
            var reason = Clean(req.Reason, 300);
            var text = $"suspendu sur tout le parc par {actor}{(reason is null ? "" : $" : {reason}")}";
            if (!store.SuspendVersion(id, automatic: false, text, actor, clock.GetUtcNow())) return Conflict("Ce pilote est déjà suspendu.");
            audit.Record(request.HttpContext, AuditActions.RolloutSuspend, v.DisplayName, text);
            return null;
        }));

        operate.MapPost("/versions/{id:long}/resume", (long id, HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit,
            TimeProvider clock, CancellationToken ct) => VersionAction(request, id, store, rollout, ct, (v, req, actor) =>
        {
            var comment = Clean(req.Comment, 300);
            if (!store.ResumeVersion(id, actor, comment, clock.GetUtcNow())) return Conflict("Ce pilote n'est pas suspendu.");
            audit.Record(request.HttpContext, AuditActions.RolloutResume, v.DisplayName,
                $"suspension levée ({v.SuspendReason}){(comment is null ? "" : $" — {comment}")}");
            return null;
        }));

        operate.MapPost("/versions/{id:long}/acknowledge", (long id, HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit,
            TimeProvider clock, CancellationToken ct) => VersionAction(request, id, store, rollout, ct, (v, req, actor) =>
        {
            var comment = Clean(req.Comment, 300);
            if (!store.AcknowledgeSuspension(id, actor, comment, clock.GetUtcNow())) return Conflict("Aucune suspension à examiner pour ce pilote.");
            audit.Record(request.HttpContext, AuditActions.RolloutAcknowledge, v.DisplayName, $"blocage maintenu{(comment is null ? "" : $" — {comment}")}");
            return null;
        }));

        admin.MapPost("/versions/{id:long}/approve", (long id, HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit,
            TimeProvider clock, CancellationToken ct) => VersionAction(request, id, store, rollout, ct, (v, req, actor) =>
        {
            var comment = Clean(req.Comment, 300);
            if (!store.ApproveVersion(id, true, actor, comment, clock.GetUtcNow())) return Conflict("Ce pilote est déjà diffusé partout.");
            audit.Record(request.HttpContext, AuditActions.RolloutApprove, v.DisplayName,
                $"autorisé pour tous les anneaux sans délai ni validation{(comment is null ? "" : $" — {comment}")}");
            return null;
        }));

        admin.MapPost("/versions/{id:long}/unapprove", (long id, HttpRequest request, ServerStore store, RolloutService rollout, AuditService audit,
            TimeProvider clock, CancellationToken ct) => VersionAction(request, id, store, rollout, ct, (v, req, actor) =>
        {
            if (!store.ApproveVersion(id, false, actor, Clean(req.Comment, 300), clock.GetUtcNow())) return Conflict("Ce pilote n'est pas diffusé partout.");
            audit.Record(request.HttpContext, AuditActions.RolloutUnapprove, v.DisplayName, "retour à la diffusion progressive");
            return null;
        }));

        // ------------------------------------------------------------------ historique, aperçu

        view.MapGet("/history", (int? offset, int? limit, ServerStore store) =>
        {
            var (total, items) = store.GetRolloutEvents(null, Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, MaxPageSize));
            return Results.Ok(new { total, items = items.Select(Event) });
        });

        // Ce qu'un poste reçoit (anneau résolu comme pour le poste lui-même) : poste 4.0 — politique (suspensions seules) et
        // décisions pour les propositions de son dernier rapport —, poste 4.0 sans décisions et poste 3.x (document borné).
        view.MapGet("/preview", (string? machine, ServerStore store, RolloutService rollout) =>
        {
            if (string.IsNullOrWhiteSpace(machine) || store.GetMachineSummary(machine.Trim()) is not { } m) return ApiEndpoints.NotFound(machine ?? "");
            var served = rollout.Serve(m.MachineId, m.Name, rolloutAware: true, decisions: true);
            var rulesMode = rollout.Serve(m.MachineId, m.Name, rolloutAware: true);
            var legacy = rollout.ServeLegacy();
            var candidates = new List<Fleet.CandidateEntry>();
            if (store.GetRawReport(m.Name) is { } raw)
            {
                try { candidates = Fleet.ReportParser.Parse(raw).Candidates; }
                catch (Fleet.InvalidReportException) { /* rapport illisible : aucune proposition à montrer */ }
            }
            var queries = candidates.Select(c => new CandidateQuery(c.Source, c.Title, c.Version, c.Kind)).ToList();
            var batch = rollout.Decide(m.MachineId, m.Name, queries);
            var request = JsonSerializer.Serialize(new { candidates = queries.Select(q => new { source = RolloutKeys.SourceKey(q.Source), title = q.Title, version = q.Version, kind = RolloutKeys.CandidateKind(q.Kind) ?? q.Kind }) });
            return Results.Ok(new
            {
                machine = m.Name,
                ring = served.Target.Ring?.Name,
                conservative = served.Target.Conservative,
                suspensions = served.Rules.Suspensions,
                holds = rulesMode.Rules.Holds,
                families = rulesMode.Rules.Families,
                holdUnknown = RolloutPolicyBuilder.HoldsNewVersions(rollout.Current().Snapshot, served.Target),
                bytes = served.Utf8?.Length ?? 0,
                policy = served.Json,
                decisions = new
                {
                    candidates = batch.Decisions.Count,
                    allowed = batch.Decisions.Count(d => d.Status == DecisionStatus.Allowed),
                    held = batch.Decisions.Count(d => d.Status == DecisionStatus.Held),
                    suspended = batch.Decisions.Count(d => d.Status == DecisionStatus.Suspended),
                    unknown = batch.Decisions.Count(d => d.Status == DecisionStatus.Unknown),
                    requestBytes = Encoding.UTF8.GetByteCount(request),
                    items = candidates.Zip(batch.Decisions, (c, d) => new
                    {
                        title = c.Title, version = c.Version, source = c.Source, kind = c.Kind, status = d.Status, availableAt = d.AvailableAt, reason = d.Reason,
                    }),
                },
                withoutDecisions = Summary(rulesMode, rollout.Limits),
                legacy = Summary(legacy, rollout.Limits),
            });
        });
    }

    // ------------------------------------------------------------------ décisions (postes 4.0)

    /// <summary>Taille maximale (décompressée) d'une demande de décisions.</summary>
    public const int MaxDecideBytes = 512 * 1024;

    private static async Task<IResult> DecideAsync(HttpRequest request, RolloutService rollout, CancellationToken ct)
    {
        var body = await BodyReader.ReadAsync(request, MaxDecideBytes, "Liste des propositions", ct);
        if (body.Error is not null) return body.Error;
        DecideRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<DecideRequest>(body.Text!, ServerStore.Json);
        }
        catch (JsonException ex)
        {
            return BadRequest($"Demande illisible : {ex.Message}");
        }
        if (req?.Candidates is not { } list) return BadRequest("« candidates » manquant (liste des propositions du poste).");
        if (list.Count > RolloutService.MaxDecisionCandidates) return BadRequest($"{RolloutService.MaxDecisionCandidates} propositions au plus par demande.");
        var headers = request.Headers;
        var machineId = headers[Jobs.JobEndpoints.MachineIdHeader].ToString() is { Length: > 0 } h ? h : req.MachineId;
        var machine = headers["X-SpokaPilote-Machine"].ToString() is { Length: > 0 } n ? n : req.Machine;
        var batch = rollout.Decide(machineId, machine, list.Select(c => new CandidateQuery(c?.Source, c?.Title, c?.Version, c?.Kind)).ToList());

        var root = batch.Section;
        root["decisions"] = new JsonArray(batch.Decisions.Select(d =>
        {
            var o = new JsonObject { ["status"] = d.Status };
            if (d.AvailableAt is { } at) o["availableAt"] = at.ToUniversalTime().ToString("O");
            if (d.Reason is not null) o["reason"] = d.Reason;
            if (d.Rule is not null) o["rule"] = d.Rule;
            return (JsonNode)o;
        }).ToArray());
        if (batch.Target.Ring is { } ring) request.HttpContext.Response.Headers["X-SpokaPilote-Ring"] = Uri.EscapeDataString(ring.Name);
        else if (batch.Target.Conservative) request.HttpContext.Response.Headers["X-SpokaPilote-Ring"] = "conservative";
        var utf8 = Encoding.UTF8.GetBytes(root.ToJsonString());
        if (utf8.Length > 1024 && headers.AcceptEncoding.ToString().Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            using var output = new MemoryStream(utf8.Length / 4 + 64);
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(utf8);
            request.HttpContext.Response.Headers.ContentEncoding = "gzip";
            request.HttpContext.Response.Headers.Vary = "Accept-Encoding";
            return Results.Bytes(output.ToArray(), "application/json; charset=utf-8");
        }
        return Results.Bytes(utf8, "application/json; charset=utf-8");
    }

    /// <summary>Taille et forme d'une politique servie (aperçu).</summary>
    private static object Summary(ServedPolicy p, RolloutLimits limits) => new
    {
        bytes = p.Utf8?.Length ?? 0,
        gzipBytes = p.Gzip?.Length ?? p.Utf8?.Length ?? 0,
        rules = p.Rules.Rules.Count,
        form = p.Rules.Form,
        sourceHolds = p.Rules.SourceHolds,
        heldVersions = p.Rules.HeldVersions,
        datedHolds = p.Rules.Holds,
        undatedHolds = p.Rules.UndatedHolds,
        families = p.Rules.Families,
        omittedFamilies = p.Rules.OmittedFamilies,
        suspensions = p.Rules.Suspensions,
        maxRules = limits.MaxRules,
        maxBytes = limits.MaxBytes,
    };

    /// <summary>Corps de POST /api/rollout/decide.</summary>
    public sealed class DecideRequest
    {
        /// <summary>Identité (si les en-têtes X-SpokaPilote-Machine-Id / X-SpokaPilote-Machine ne sont pas envoyés).</summary>
        public string? MachineId { get; set; }
        public string? Machine { get; set; }
        public List<DecideCandidate?>? Candidates { get; set; }
    }

    /// <summary>Proposition : source (nom de source du poste : WindowsUpdate, Dell...), titre, version, type (MissingDriver, Update, Optional, Firmware).</summary>
    public sealed class DecideCandidate
    {
        public string? Source { get; set; }
        public string? Title { get; set; }
        public string? Version { get; set; }
        public string? Kind { get; set; }
    }

    // ------------------------------------------------------------------ utilitaires

    /// <summary>Anneaux suggérés : délais seuls (la validation dans l'anneau précédent s'active ensuite, anneau par anneau).</summary>
    internal static readonly ServerStore.RingInput[] SuggestedRings =
    [
        new("Pilote", 0, false, 1, 0, "Quelques postes du service informatique et des volontaires : reçoivent les nouvelles versions aussitôt.", []),
        new("Précoce", 3, false, 3, 0, "Un échantillon représentatif de chaque modèle : 3 jours de recul.", []),
        new("Général", 10, false, 5, 0, "Le reste du parc (et les postes sans groupe d'anneau) : 10 jours de recul.", []),
    ];

    private static IResult RingNotFound() => Results.Json(new ApiError("Anneau introuvable."), statusCode: StatusCodes.Status404NotFound);
    private static IResult VersionNotFound() => Results.Json(new ApiError("Pilote introuvable."), statusCode: StatusCodes.Status404NotFound);

    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = new string(text.Trim().Where(c => !char.IsControl(c)).ToArray());
        return s.Length <= max ? s : s[..max];
    }

    private static async Task<IResult> VersionAction(HttpRequest request, long id, ServerStore store, RolloutService rollout, CancellationToken ct,
        Func<RolloutVersion, RolloutActionRequest, string, IResult?> action)
    {
        if (store.GetRolloutVersion(id) is not { } v) return VersionNotFound();
        var req = new RolloutActionRequest();
        if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            var (body, error) = await ReadAsync<RolloutActionRequest>(request, ct);
            if (error is not null) return error;
            req = body!;
        }
        var actor = Principal.Of(request.HttpContext)!.Actor;
        if (action(v, req, actor) is { } failure) return failure;
        rollout.Changed(urgent: true);
        var view = rollout.Current();
        return view.ById.TryGetValue(id, out var plan) ? Results.Ok(Row(plan, view, rollout.Zone)) : Results.NoContent();
    }

    private static IResult? ValidateRing(RingRequest req, ServerStore store, out ServerStore.RingInput input)
    {
        var name = new string((req.Name ?? "").Trim().Where(c => !char.IsControl(c)).ToArray());
        var description = Clean(req.Description, 300);
        var groups = (req.Groups ?? new List<long>()).Distinct().ToList();
        input = new ServerStore.RingInput(name, req.DelayDays ?? 0, req.RequireValidation, req.MinSuccesses ?? 1, req.MaxFailures ?? 0, description, groups);
        if (name.Length is 0 or > 60) return BadRequest("Nom de l'anneau obligatoire (60 caractères au plus).");
        if (input.DelayDays is < 0 or > 365) return BadRequest("Délai : 0 à 365 jours.");
        if (input.MinSuccesses is < 1 or > 10_000) return BadRequest("Installations réussies exigées : 1 à 10 000.");
        if (input.MaxFailures is < 0 or > 10_000) return BadRequest("Échecs tolérés : 0 à 10 000.");
        if (groups.Count > 100) return BadRequest("100 groupes au plus par anneau.");
        var unknown = groups.Where(g => !store.GroupExists(g)).ToList();
        if (unknown.Count > 0) return BadRequest($"Groupe(s) inconnu(s) : {string.Join(", ", unknown)}.");
        return null;
    }

    private static string RingText(ServerStore.RingInput r, ServerStore store)
    {
        var groups = store.GetGroups().Where(g => r.GroupIds.Contains(g.Id)).Select(g => g.Name).ToList();
        return $"délai {RolloutPlanner.Plural(r.DelayDays, "jour", "jours")}" +
               (r.RequireValidation ? $", validation exigée ({r.MinSuccesses} réussite(s), {r.MaxFailures} échec(s) toléré(s))" : "") +
               $", groupes : {(groups.Count == 0 ? "aucun" : string.Join(", ", groups))}";
    }

    private static string RingLabel(ServerStore store, long? id) =>
        store.GetRings() is { Count: > 0 } rings ? (rings.FirstOrDefault(r => r.Id == id) ?? rings[^1]).Name : "aucun anneau";

    private static object Overview(ServerStore store, RolloutService rollout)
    {
        var view = rollout.Current();
        var settings = store.GetRolloutSettings();
        var rings = store.GetRings();
        var (perRing, conflicts, total) = store.GetRingMachineCounts();
        var groups = store.GetGroups().ToDictionary(g => g.Id, g => g.Name);
        var defaultRing = rings.Count == 0 ? null : rings.FirstOrDefault(r => r.Id == settings.DefaultRingId) ?? rings[^1];
        return new
        {
            settings,
            active = settings.Enabled && rings.Count > 0,
            defaultRingId = defaultRing?.Id,
            machines = total,
            conflicts,
            zone = rollout.Zone.Id,
            inRollout = view.InRollout,
            suspended = view.Suspended,
            attention = view.Attention,
            versions = view.Plans.Count,
            rings = rings.Select((r, i) => new
            {
                id = r.Id,
                name = r.Name,
                position = i + 1,
                delayDays = r.DelayDays,
                requireValidation = r.RequireValidation,
                validationRing = r.RequireValidation && i > 0 ? rings[i - 1].Name : null,
                minSuccesses = r.MinSuccesses,
                maxFailures = r.MaxFailures,
                description = r.Description,
                groups = r.GroupIds.Where(groups.ContainsKey).Select(g => new { id = g, name = groups[g] }),
                machineCount = perRing.GetValueOrDefault(r.Id),
                isDefault = defaultRing?.Id == r.Id,
                updatedAt = r.UpdatedAt,
            }),
        };
    }

    private static object Stat(RingStat s) => new { successes = s.Successes, failures = s.Failures, regressions = s.Regressions };

    private static object Row(VersionPlan p, RolloutService.View view, TimeZoneInfo zone)
    {
        var v = p.Version;
        var totals = p.Rings.Select(r => r.Stat).Append(view.Snapshot.Stat(v.Id, 0)).ToList();
        return new
        {
            id = v.Id,
            driver = v.DisplayName,
            title = v.Title,
            version = v.Version,
            source = v.Source,
            sourceLabel = RolloutKeys.SourceLabel(v.Source),
            family = v.Family,
            kind = v.Kind,
            category = v.Category,
            firstSeen = v.FirstSeen,
            firstMachine = v.FirstMachine,
            lastSeen = v.LastSeen,
            status = view.Snapshot.RingsActive || v.Suspended ? p.Status : "untracked",
            attention = v.NeedsAttention,
            suspension = v.Suspended
                ? new { automatic = v.SuspendAuto, reason = v.SuspendReason, at = v.SuspendedAt, by = v.SuspendedBy, acknowledgedAt = v.AcknowledgedAt, acknowledgedBy = v.AcknowledgedBy }
                : null,
            approval = v.ApprovedAt is null ? null : new { at = v.ApprovedAt, by = v.ApprovedBy },
            resumedAt = v.ResumedAt,
            resumedBy = v.ResumedBy,
            rings = p.Rings.Select(r => new
            {
                ringId = r.Ring.Id,
                name = r.Ring.Name,
                state = r.StateCode,
                label = RolloutPlanner.Label(r, v, zone),
                availableAt = r.AvailableAt,
                eligible = r.Eligible,
                successes = r.Stat.Successes,
                failures = r.Stat.Failures,
                regressions = r.Stat.Regressions,
            }),
            totals = new { successes = totals.Sum(t => t.Successes), failures = totals.Sum(t => t.Failures), regressions = totals.Sum(t => t.Regressions) },
        };
    }

    private static object Event(RolloutEvent e) => new
    {
        id = e.Id,
        at = e.At,
        versionId = e.VersionId,
        driver = e.Driver,
        action = e.Action,
        actionLabel = e.Action switch
        {
            "suspend" => "Suspendu",
            "auto-suspend" => "Suspendu automatiquement",
            "resume" => "Diffusion reprise",
            "acknowledge" => "Blocage maintenu",
            "approve" => "Diffusé partout",
            "unapprove" => "Diffusion forcée annulée",
            "enable" => "Diffusion par anneaux activée",
            "disable" => "Diffusion par anneaux désactivée",
            _ => e.Action,
        },
        actor = e.Actor,
        detail = e.Detail,
    };
}
