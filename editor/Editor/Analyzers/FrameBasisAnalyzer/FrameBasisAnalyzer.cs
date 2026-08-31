// re-verified 2026-05-25: Microsoft.CodeAnalysis using-directives unchanged from prior session. The banned approach is downgrading the analyzer csproj's PackageReference to Microsoft.CodeAnalysis.CSharp 4.7.0 (falsified); FrameBasisAnalyzer.csproj remains at 5.0.0-2.final (verified one-up at line 22 of that csproj this session). This rewrite strips the diagnostic trace instrumentation and applies the IsVector3 root-cause fix.
using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SBEP.Analyzers.FrameBasis;

/// <summary>
/// Frame-basis analyzer for the gravity-hull subsystem.
///
/// Rule FBA001 — untyped Vector3 declarations in gravity-hull files.
/// Any local / parameter / field / property declaration whose type is
/// <c>Vector3</c> inside a scanned file is reported as a warning. The
/// fix is to pick one of <c>WorldVec3</c>, <c>ShipLocalVec3</c>,
/// <c>HullFrameVec3</c>, or <c>Vector3Extent</c> — the frame is then
/// part of the type and cannot be silently mixed.
///
/// Rule FBA002 — cross-frame arithmetic.
/// Invocations and binary operators that mix two distinct frame-tagged
/// types (e.g. <c>Vector3.Distance(WorldVec3, ShipLocalVec3)</c> or
/// <c>worldVec + shipLocalVec</c>) are reported. These are fire-anywhere
/// — even outside the path filter — because cross-frame arithmetic is
/// always wrong regardless of where it appears.
///
/// Severity is Warning in Phase 2 to allow the mechanical refactor to
/// land file-by-file without breaking the build. Phase 4 promotes to
/// Error.
///
/// Scanned paths (FBA001 only — FBA002 fires anywhere a frame-tagged
/// type appears):
///   Code/SBEP/Ship/GravityHull/**/*.cs
///   Code/SBEP/Tests/GravityHull/**/*.cs
///   Code/SBEP/Debug/StepDebugger*.cs
///   Code/SBEP/Debug/GravityHullDebugOverlay.cs
/// </summary>
[DiagnosticAnalyzer( LanguageNames.CSharp )]
public sealed class FrameBasisAnalyzer : DiagnosticAnalyzer
{
	public const string UntypedVector3Id = "FBA001";
	public const string CrossFrameArithmeticId = "FBA002";
	public const string ForbiddenPassengerBodyMutationId = "FBA003";
	public const string ForbiddenAttachStateMutationId = "FBA004";
	// [HANDOFF-TRUST: FBA005 Phase 4 ships per signed-off design § 9 + plan.md § 5. The rule is an INNER layer of FBA003 — fires when a body.Velocity write inside BodyVelocityWriter is missing the [GhWorldFrameWriter("<reason>")] attribute (the audit-readable marker that names the legitimacy reason). Falsification sentinel: .claude/session-state/agents/866aca04.../falsification-passed/gh-walk-in-hull-frame-phase-4-attach-detach-retirement.json — H1prime polled-edge architecture confirmed; this analyzer is independent scaffolding.]
	public const string ForbiddenUndecoratedWorldFrameWriterId = "FBA005";

	// FBA005 — fully-qualified name of the GhWorldFrameWriter marker attribute.
	// MUST match the attribute's namespace + type exactly so IMethodSymbol.GetAttributes()
	// substring-checks resolve unambiguously. The attribute is declared in
	// Code/SBEP/Ship/GravityHull/Math/GhWorldFrameWriterAttribute.cs.
	private const string GhWorldFrameWriterAttributeFullName = "SBEP.Ship.GravityHull.GhWorldFrameWriterAttribute";

	private static readonly string[] FrameTypeNames =
	{
		"WorldVec3",
		"ShipLocalVec3",
		"HullFrameVec3",
		"RelativeVec3",
		"Vector3Extent",
	};

	// FBA003 — only these types are allowed to write Rigidbody.Velocity /
	// Rigidbody.AngularVelocity / Rigidbody.MotionEnabled from inside the
	// gravity-hull subsystem. Whitelist is matched against the enclosing
	// type's full display name (namespace + type) so file-renames / partial
	// classes can't sneak around it.
	private static readonly string[] AllowedBodyMutatorTypes =
	{
		"SBEP.Ship.GravityHull.BodyVelocityWriter",
		"SBEP.Ship.GravityHull.GravityHullAttachState",
	};

	// FBA003 — Rigidbody / PhysicsBody members that may not be assigned outside
	// the whitelist. Matched against the symbol's containing-type + name so a
	// future engine rename (e.g. PhysicsBody.Velocity → .LinearVelocity) is
	// still caught.
	private static readonly (string ContainingType, string Member)[] ForbiddenSetters =
	{
		("Sandbox.Rigidbody",   "Velocity"),
		("Sandbox.Rigidbody",   "AngularVelocity"),
		("Sandbox.Rigidbody",   "MotionEnabled"),
		("Sandbox.PhysicsBody", "Velocity"),
		("Sandbox.PhysicsBody", "AngularVelocity"),
		("Sandbox.PhysicsBody", "MotionEnabled"),
	};

	// FBA004 — these fields constitute the gravity-hull attach state machine.
	// They are private today and only legal writers are inside the owning
	// type. FBA004 makes a careless `internal` promotion not silently unlock
	// them. Matched against (containing type, field name).
	//
	// Phase 4 (gh-determinism, 2026-05-26) — WalkInGravityHullMoveMode
	// entries removed. The 4 fields were extracted into GravityHullAttachState;
	// the mode now holds a `_attach` instance and delegates all mutation
	// into AttachState.Attach / AttachState.Detach. AttachState is now the
	// sole legal writer.
	private static readonly (string ContainingType, string Member)[] AttachStateFields =
	{
		("SBEP.Ship.GravityHull.GravityHullAttachState",    "_attachedToHull"),
		("SBEP.Ship.GravityHull.GravityHullAttachState",    "_attachedHullGameObject"),
		("SBEP.Ship.GravityHull.GravityHullAttachState",    "_savedParent"),
		("SBEP.Ship.GravityHull.GravityHullAttachState",    "_savedMotionEnabled"),
	};

	// Severity = Hidden as of 2026-05-25 (gh-fba001-suppress, audit of 244 rotating-ship-critical
	// sites found 1 real bug at WalkInGravityHullMoveMode.cs:2239 + ~20 hygiene SUSPECTs; rollout
	// deferred per docs/ai/plans/gh-fba001-rollout.md "do only what is necessary" direction). Hidden
	// keeps the diagnostic registered so feed.compile audits still see it, but stops console spam.
	// Re-promote to Warning when running the next staged FBA001 cleanup pass.
	private static readonly DiagnosticDescriptor UntypedVector3Rule = new(
		id: UntypedVector3Id,
		title: "Untyped Vector3 in gravity-hull code",
		messageFormat: "Vector3 '{0}' in gravity-hull code is missing its frame tag — pick WorldVec3 / ShipLocalVec3 / HullFrameVec3 / Vector3Extent",
		category: "SBEP.GravityHull",
		defaultSeverity: DiagnosticSeverity.Hidden,
		isEnabledByDefault: true,
		description: "Inside the gravity-hull subsystem every Vector3 must carry an explicit frame tag. " +
			"Vector3.Distance(worldPos, shipLocalPos) was the Phase 4 harness invalidator — frame-typed wrappers make it a compile error." );

	// [HANDOFF-TRUST: user-approved minimal Step 6 — FBA002 promoted to Error per cliff-B decision 2026-05-25; the cascade migrated the 9 subtraction sites to typed RelativeVelocity helper in Step 3, so this flip is expected to produce ZERO new build errors while compile-time-enforcing the ShouldBeAttachedCore anchor-bug pattern; FBA001 stays at Warning per user direction "do only what is necessary" — broader FBA001 escalation deferred to follow-up session per docs/ai/plans/gh-fba001-rollout.md]
	private static readonly DiagnosticDescriptor CrossFrameRule = new(
		id: CrossFrameArithmeticId,
		title: "Cross-frame arithmetic",
		messageFormat: "Cross-frame arithmetic between {0} and {1} — convert through GravityHullVolume first",
		category: "SBEP.GravityHull",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "Mixing two distinct frame-tagged vector types (WorldVec3 / ShipLocalVec3 / HullFrameVec3 / Vector3Extent) " +
			"in arithmetic or distance/dot calls is silently meaningless. Convert through the hull's WorldTransform first. " +
			"Promoted to Error 2026-05-25 (R5 Phase 4 Step 6) — this is the compile-time enforcement of the anchor case " +
			"(WalkInGravityHullMoveMode.ShouldBeAttachedCore) that motivated the typed-cascade plan." );

	private static readonly DiagnosticDescriptor ForbiddenPassengerBodyMutationRule = new(
		id: ForbiddenPassengerBodyMutationId,
		title: "Forbidden passenger body mutation in gravity-hull code",
		messageFormat: "Direct '{0}' write in gravity-hull code — route through SBEP.Ship.GravityHull.BodyVelocityWriter (the only legal writer outside GravityHullAttachState)",
		category: "SBEP.GravityHull",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "Every Rigidbody.Velocity / .AngularVelocity / .MotionEnabled write inside the gravity-hull subsystem " +
			"(Code/SBEP/Ship/GravityHull/**, Code/SBEP/Tests/GravityHull/**, Code/SBEP/Debug/StepDebugger*, GravityHullDebugOverlay) " +
			"must go through BodyVelocityWriter — the single chokepoint that carries the slew/snap logic + post-write fling " +
			"invariant + structured trace. Direct writes are how the fling regression class keeps coming back. The only legal " +
			"writer-types are BodyVelocityWriter itself and GravityHullAttachState (the encapsulated attach/detach state machine)." );

	private static readonly DiagnosticDescriptor ForbiddenAttachStateMutationRule = new(
		id: ForbiddenAttachStateMutationId,
		title: "Forbidden attach-state field mutation",
		messageFormat: "Attach-state field '{0}' written outside its owning type — these fields are private today; FBA004 catches a careless internal promotion before it silently unlocks the fling-class regression",
		category: "SBEP.GravityHull",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "_attachedToHull / _attachedHullGameObject / _savedParent / _savedMotionEnabled form the gravity-hull " +
			"attach state machine. They must only be written from inside their owning type. FBA004 makes a careless `internal` " +
			"promotion or partial-class extension not silently unlock external mutation." );

	// FBA005 — INNER layer on top of FBA003. FBA003 catches body.Velocity writes
	// OUTSIDE BodyVelocityWriter. FBA005 catches body.Velocity writes INSIDE
	// BodyVelocityWriter from a method that is NOT decorated with [GhWorldFrameWriter].
	// Together they collapse the codebase to "every world-frame body.Velocity write
	// lives in a named, audit-readable, attribute-declared method."
	//
	// Phase 4 of gh-walk-in-hull-frame-rewrite ships exactly three exempt sites:
	//   - ComposeWorldDetachVelocity (rewrite-mode detach; NoFlingExit postcondition)
	//   - ComposeLegacyHandoffVelocity (legacy handoff; § 11.3 defense-in-depth fallback)
	//   - SnapForTestFixture (test-only velocity injection; non-production path)
	private static readonly DiagnosticDescriptor ForbiddenUndecoratedWorldFrameWriterRule = new(
		id: ForbiddenUndecoratedWorldFrameWriterId,
		title: "Forbidden world-frame body.Velocity write outside [GhWorldFrameWriter]-marked sites",
		messageFormat: "body.Velocity write inside BodyVelocityWriter from method '{0}' — but method is not decorated with [GhWorldFrameWriter('<reason>')]. The only allowed world-frame writers are ComposeWorldDetachVelocity (Phase 4 detach), ComposeLegacyHandoffVelocity (legacy fallback per § 11.3), and SnapForTestFixture (test-only). Add [GhWorldFrameWriter('<your reason here>')] OR route the write through one of those three methods.",
		category: "SBEP.GravityHull",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "FBA005 extends FBA003. Inside BodyVelocityWriter, body.Velocity writes are allowed only from " +
			"[GhWorldFrameWriter('<reason>')]-decorated methods. The attribute's Reason string documents WHY the write is allowed; " +
			"it must be non-null and non-empty so reviewers can grep for the marker and read the justification without opening the " +
			"method body. Phase 4 binding per docs/sbep-reference/gravity-hull-frame-authoritative-design.md section 9." );

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
		=> ImmutableArray.Create( UntypedVector3Rule, CrossFrameRule, ForbiddenPassengerBodyMutationRule, ForbiddenAttachStateMutationRule, ForbiddenUndecoratedWorldFrameWriterRule );

	public override void Initialize( AnalysisContext context )
	{
		context.ConfigureGeneratedCodeAnalysis( GeneratedCodeAnalysisFlags.None );
		context.EnableConcurrentExecution();

		// FBA001 — untyped Vector3 in scanned paths.
		context.RegisterSyntaxNodeAction( AnalyzeLocalDeclaration, SyntaxKind.LocalDeclarationStatement );
		context.RegisterSyntaxNodeAction( AnalyzeParameter, SyntaxKind.Parameter );
		context.RegisterSyntaxNodeAction( AnalyzeField, SyntaxKind.FieldDeclaration );
		context.RegisterSyntaxNodeAction( AnalyzeProperty, SyntaxKind.PropertyDeclaration );

		// FBA002 — cross-frame arithmetic (fires anywhere).
		context.RegisterSyntaxNodeAction( AnalyzeBinary, SyntaxKind.AddExpression, SyntaxKind.SubtractExpression );
		context.RegisterSyntaxNodeAction( AnalyzeInvocation, SyntaxKind.InvocationExpression );

		// FBA003 + FBA004 — forbidden mutations (scanned paths only).
		context.RegisterSyntaxNodeAction( AnalyzeAssignment, SyntaxKind.SimpleAssignmentExpression );
	}

	private static bool IsScannedFile( SyntaxTree tree )
	{
		var path = tree.FilePath?.Replace( '\\', '/' ) ?? string.Empty;
		if ( path.Length == 0 ) return false;
		if ( path.Contains( "/Code/SBEP/Ship/GravityHull/" ) ) return true;
		if ( path.Contains( "/Code/SBEP/Tests/GravityHull/" ) ) return true;

		var fileName = System.IO.Path.GetFileName( path );
		if ( path.Contains( "/Code/SBEP/Debug/" ) )
		{
			if ( fileName.StartsWith( "StepDebugger", StringComparison.Ordinal ) ) return true;
			if ( fileName == "GravityHullDebugOverlay.cs" ) return true;
		}
		return false;
	}

	// [HANDOFF-TRUST: ROOT-CAUSE fix re-verified 2026-05-25 via FBA-TRACE n=9..n=25 against M:/tmp/fba-trace.log on round-2 instrumented compile — every Vector3 declaration in scanned GravityHull files resolved to name='Vector3' ns='<global namespace>' kind=Struct sym=NamedType. S&Box's Vector3 lives at global scope, not in Sandbox namespace. The previous IsScannedFile filter, RegisterSyntaxNodeAction wiring, SemanticModel resolution, and CompilationWithAnalyzers retention were ALL verified-working (analyzer_diagnostics_count=0 persisted only because IsVector3 silently rejected every type). Fix: accept ns.IsGlobalNamespace; defensively keep "Sandbox" / "Sandbox.*" allow-list for future engine refactors that namespace Vector3.]
	private static bool IsVector3( ITypeSymbol type )
	{
		if ( type is null ) return false;
		if ( type.Name != "Vector3" ) return false;
		var ns = type.ContainingNamespace;
		if ( ns is null || ns.IsGlobalNamespace ) return true;
		var nsName = ns.ToDisplayString();
		return nsName == "Sandbox"
			|| nsName.StartsWith( "Sandbox.", StringComparison.Ordinal );
	}

	private static string FrameTypeName( ITypeSymbol type )
	{
		if ( type is null ) return null;
		return FrameTypeNames.FirstOrDefault( n => n == type.Name );
	}

	private static void AnalyzeLocalDeclaration( SyntaxNodeAnalysisContext ctx )
	{
		if ( !IsScannedFile( ctx.Node.SyntaxTree ) ) return;
		var decl = (LocalDeclarationStatementSyntax)ctx.Node;
		var typeInfo = ctx.SemanticModel.GetTypeInfo( decl.Declaration.Type, ctx.CancellationToken );
		if ( !IsVector3( typeInfo.Type ) ) return;
		foreach ( var v in decl.Declaration.Variables )
		{
			ctx.ReportDiagnostic( Diagnostic.Create( UntypedVector3Rule, v.Identifier.GetLocation(), v.Identifier.Text ) );
		}
	}

	private static void AnalyzeParameter( SyntaxNodeAnalysisContext ctx )
	{
		if ( !IsScannedFile( ctx.Node.SyntaxTree ) ) return;
		var param = (ParameterSyntax)ctx.Node;
		if ( param.Type is null ) return;
		var typeInfo = ctx.SemanticModel.GetTypeInfo( param.Type, ctx.CancellationToken );
		if ( !IsVector3( typeInfo.Type ) ) return;
		ctx.ReportDiagnostic( Diagnostic.Create( UntypedVector3Rule, param.Identifier.GetLocation(), param.Identifier.Text ) );
	}

	private static void AnalyzeField( SyntaxNodeAnalysisContext ctx )
	{
		if ( !IsScannedFile( ctx.Node.SyntaxTree ) ) return;
		var field = (FieldDeclarationSyntax)ctx.Node;
		var typeInfo = ctx.SemanticModel.GetTypeInfo( field.Declaration.Type, ctx.CancellationToken );
		if ( !IsVector3( typeInfo.Type ) ) return;
		foreach ( var v in field.Declaration.Variables )
		{
			ctx.ReportDiagnostic( Diagnostic.Create( UntypedVector3Rule, v.Identifier.GetLocation(), v.Identifier.Text ) );
		}
	}

	private static void AnalyzeProperty( SyntaxNodeAnalysisContext ctx )
	{
		if ( !IsScannedFile( ctx.Node.SyntaxTree ) ) return;
		var prop = (PropertyDeclarationSyntax)ctx.Node;
		var typeInfo = ctx.SemanticModel.GetTypeInfo( prop.Type, ctx.CancellationToken );
		if ( !IsVector3( typeInfo.Type ) ) return;
		ctx.ReportDiagnostic( Diagnostic.Create( UntypedVector3Rule, prop.Identifier.GetLocation(), prop.Identifier.Text ) );
	}

	private static void AnalyzeBinary( SyntaxNodeAnalysisContext ctx )
	{
		var bin = (BinaryExpressionSyntax)ctx.Node;
		var leftType = ctx.SemanticModel.GetTypeInfo( bin.Left, ctx.CancellationToken ).Type;
		var rightType = ctx.SemanticModel.GetTypeInfo( bin.Right, ctx.CancellationToken ).Type;
		var leftFrame = FrameTypeName( leftType );
		var rightFrame = FrameTypeName( rightType );
		if ( leftFrame is null || rightFrame is null ) return;
		if ( leftFrame == rightFrame ) return;
		ctx.ReportDiagnostic( Diagnostic.Create( CrossFrameRule, bin.GetLocation(), leftFrame, rightFrame ) );
	}

	private static void AnalyzeInvocation( SyntaxNodeAnalysisContext ctx )
	{
		var inv = (InvocationExpressionSyntax)ctx.Node;
		var args = inv.ArgumentList.Arguments;
		if ( args.Count < 2 ) return;
		string seenFrame = null;
		foreach ( var arg in args )
		{
			var argType = ctx.SemanticModel.GetTypeInfo( arg.Expression, ctx.CancellationToken ).Type;
			var frame = FrameTypeName( argType );
			if ( frame is null ) continue;
			if ( seenFrame is null ) { seenFrame = frame; continue; }
			if ( seenFrame != frame )
			{
				ctx.ReportDiagnostic( Diagnostic.Create( CrossFrameRule, inv.GetLocation(), seenFrame, frame ) );
				return;
			}
		}
	}

	// FBA003 + FBA004 — forbidden mutations. Triggered on every assignment in
	// scanned files; resolves the LHS symbol and matches against the forbidden
	// lists. Whitelists by enclosing type so BodyVelocityWriter / GravityHullAttachState
	// can write freely (they ARE the legal writers).
	private static void AnalyzeAssignment( SyntaxNodeAnalysisContext ctx )
	{
		if ( !IsScannedFile( ctx.Node.SyntaxTree ) ) return;
		var assign = (AssignmentExpressionSyntax)ctx.Node;
		var symbol = ctx.SemanticModel.GetSymbolInfo( assign.Left, ctx.CancellationToken ).Symbol;
		if ( symbol is null ) return;

		var containingType = symbol.ContainingType?.ToDisplayString();
		if ( containingType is null ) return;
		var memberName = symbol.Name;

		// FBA003 — Rigidbody.Velocity / AngularVelocity / MotionEnabled
		foreach ( var (ct, m) in ForbiddenSetters )
		{
			if ( containingType == ct && memberName == m )
			{
				if ( IsWriteAllowedFromEnclosingType( ctx ) )
				{
					// FBA005 INNER LAYER — only applies to Velocity writes per design § 9.1
					// (AngularVelocity + MotionEnabled writes are intentionally excluded; § 9.5).
					// Enclosing type is BodyVelocityWriter (we know because IsWriteAllowedFromEnclosingType
					// returned true and only that type writes Velocity). Check if enclosing method
					// is decorated with [GhWorldFrameWriter('<non-empty reason>')]. If not, fire FBA005.
					if ( memberName == "Velocity" && EnclosingTypeDisplayString( ctx ) == "SBEP.Ship.GravityHull.BodyVelocityWriter" )
					{
						var enclosingMethod = EnclosingMethodSymbol( ctx );
						if ( enclosingMethod is null || !HasGhWorldFrameWriterAttribute( enclosingMethod ) )
						{
							ctx.ReportDiagnostic( Diagnostic.Create(
								ForbiddenUndecoratedWorldFrameWriterRule,
								assign.GetLocation(),
								enclosingMethod?.Name ?? "<unknown>" ) );
						}
					}
					return;
				}
				ctx.ReportDiagnostic( Diagnostic.Create(
					ForbiddenPassengerBodyMutationRule,
					assign.GetLocation(),
					$"{ct}.{m}" ) );
				return;
			}
		}

		// FBA004 — _attachedToHull et al.
		foreach ( var (ct, m) in AttachStateFields )
		{
			if ( containingType == ct && memberName == m )
			{
				// Allowed: writer's enclosing type IS the field's owning type.
				var enclosingType = EnclosingTypeDisplayString( ctx );
				if ( enclosingType == ct ) return;
				ctx.ReportDiagnostic( Diagnostic.Create(
					ForbiddenAttachStateMutationRule,
					assign.GetLocation(),
					$"{ct}.{m}" ) );
				return;
			}
		}
	}

	private static bool IsWriteAllowedFromEnclosingType( SyntaxNodeAnalysisContext ctx )
	{
		var enclosingType = EnclosingTypeDisplayString( ctx );
		if ( enclosingType is null ) return false;
		foreach ( var allowed in AllowedBodyMutatorTypes )
			if ( enclosingType == allowed ) return true;
		return false;
	}

	private static string EnclosingTypeDisplayString( SyntaxNodeAnalysisContext ctx )
	{
		// Walk up the syntax tree to the enclosing TypeDeclarationSyntax and
		// resolve via SemanticModel — survives nested types and partial classes.
		var node = ctx.Node.Parent;
		while ( node is not null && node is not TypeDeclarationSyntax )
			node = node.Parent;
		if ( node is not TypeDeclarationSyntax typeDecl ) return null;
		var sym = ctx.SemanticModel.GetDeclaredSymbol( typeDecl, ctx.CancellationToken );
		return sym?.ToDisplayString();
	}

	// FBA005 — walk up to the enclosing method declaration and return its IMethodSymbol.
	// Returns null when the assignment is at a top-level / field-initializer / property-initializer
	// site without an enclosing method.
	private static IMethodSymbol EnclosingMethodSymbol( SyntaxNodeAnalysisContext ctx )
	{
		var node = ctx.Node.Parent;
		while ( node is not null && node is not MethodDeclarationSyntax )
			node = node.Parent;
		if ( node is not MethodDeclarationSyntax methodDecl ) return null;
		return ctx.SemanticModel.GetDeclaredSymbol( methodDecl, ctx.CancellationToken );
	}

	// FBA005 — true when the method is decorated with [GhWorldFrameWriter('<non-empty reason>')].
	// The attribute's constructor takes a required string; an empty/null reason is treated as
	// a missing attribute per design § 9.2 ("Reason argument is REQUIRED by the constructor (no
	// parameterless overload). The analyzer additionally flags [GhWorldFrameWriter('')] and
	// [GhWorldFrameWriter(null)] to prevent 'satisfy the analyzer with a no-op string' workarounds.").
	private static bool HasGhWorldFrameWriterAttribute( IMethodSymbol method )
	{
		if ( method is null ) return false;
		foreach ( var attr in method.GetAttributes() )
		{
			var attrType = attr.AttributeClass?.ToDisplayString();
			if ( attrType is null ) continue;
			if ( attrType != GhWorldFrameWriterAttributeFullName ) continue;
			// Reason is the single constructor argument. Reject null / empty.
			if ( attr.ConstructorArguments.Length == 0 ) continue;
			var reason = attr.ConstructorArguments[0].Value as string;
			if ( !string.IsNullOrWhiteSpace( reason ) ) return true;
		}
		return false;
	}
}
