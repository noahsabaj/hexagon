using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox;   // Package.UploadFile is an extension method declared in this namespace.

namespace Editor.Mcp;

/// <summary>
/// Publishing automation for the Hexagon packages, exposed over the editor's MCP server so a release
/// can be driven without clicking through the publish wizard.
/// <para>
/// s&amp;box has no command-line publish path — <c>sbox-dev.exe</c> only understands
/// <c>-generatesolution</c>, <c>-project</c> and <c>-test</c> — and the shipped <c>package</c> toolset
/// is read-only. Publishing goes through <see cref="ProjectPublisher"/>, which uses the editor's
/// authenticated session. So this removes the manual wizard, not the editor: the editor must be running
/// and signed in, and this cannot run headless in CI.
/// </para>
/// <para>
/// Tools are discovered from every loaded editor assembly (<c>ToolRegistry</c> enumerates
/// <c>EditorTypeLibrary.GetMethodsWithAttribute&lt;McpToolAttribute&gt;()</c>), which is why an addon
/// can register its own toolset at all.
/// </para>
/// </summary>
[McpToolset( "hexagon_release", "Hexagon release automation - inspect and publish package revisions to sbox.game" )]
public static class HexagonPublishTools
{
	/// <summary>
	/// Lists the projects currently open in the editor that can be published, with the ident
	/// <c>publish_package</c> expects. Use this first rather than guessing an ident.
	/// </summary>
	[McpTool.ReadOnly( "list_publishable_projects" )]
	public static ProjectSummary[] ListPublishableProjects()
	{
		return EditorUtility.Projects.GetAll()
			.Where( project => project?.Config is not null )
			.Select( project => new ProjectSummary
			{
				Ident = project.Config.FullIdent,
				SourcePublish = project.IsSourcePublish(),
				StandaloneOnly = project.Config.IsStandaloneOnly,
				ConfigPath = project.ConfigFilePath
			} )
			.OrderBy( summary => summary.Ident, StringComparer.Ordinal )
			.ToArray();
	}

	/// <summary>
	/// Publishes a new revision of a package to sbox.game. This is an outward, public action: it makes
	/// the current working tree the code that servers and clients will download. It is deliberately not
	/// marked read-only so the client can prompt.
	/// </summary>
	/// <param name="ident">Package ident, e.g. 'kbj.hexagon'. Get it from list_publishable_projects.</param>
	/// <param name="changeTitle">Revision title, shown in the package's change history.</param>
	/// <param name="changeDetail">Optional longer description of what changed.</param>
	/// <param name="compileFirst">Compile the project before publishing, as the wizard does, so compiler diagnostics appear in the editor before anything is uploaded.</param>
	[McpTool( "publish_package" )]
	public static async Task<PublishOutcome> PublishPackage(
		string ident,
		string changeTitle,
		string? changeDetail = null,
		bool compileFirst = true )
	{
		if ( string.IsNullOrWhiteSpace( ident ) )
			throw new ArgumentException( "An package ident is required, e.g. 'kbj.hexagon'.", nameof( ident ) );
		if ( string.IsNullOrWhiteSpace( changeTitle ) )
			throw new ArgumentException( "A change title is required so the revision is identifiable.", nameof( changeTitle ) );

		var project = EditorUtility.Projects.GetAll()
			.FirstOrDefault( candidate => candidate?.Config is not null
				&& string.Equals( candidate.Config.FullIdent, ident, StringComparison.OrdinalIgnoreCase ) );

		if ( project is null )
		{
			var known = string.Join( ", ", ListPublishableProjects().Select( summary => summary.Ident ) );
			throw new Exception( $"No open project has ident '{ident}'. Open projects: {known}" );
		}

		if ( project.Config.IsStandaloneOnly )
			throw new Exception( $"'{ident}' is standalone-only and cannot be published as a package." );

		var sourcePublish = project.IsSourcePublish();

		// A SOURCE publish (library) ships .cs files and needs nothing else. An ASSET publish (a game)
		// must additionally carry its COMPILED code, which the wizard assembles here rather than inside
		// ProjectPublisher: a `.bin/manifest.json` naming the assemblies in order, then a `.xml` and a
		// serialized `.cll` archive per assembly. Publishing a game WITHOUT them posts a manifest of
		// assets and no code — on 2026-07-29 that silently stripped kbj.hl2rp's assemblies and the
		// dedicated server refused to boot with "This game has no precompiled assemblies or code
		// archives!". Everything below mirrors PublishPage.2a.Compile.DoProjectCompile.
		var compileLog = new System.Text.StringBuilder();
		var assemblyFiles = new Dictionary<string, object>();
		var codePackages = new List<string>();
		CompilerOutput[]? compiled = null;

		if ( compileFirst || !sourcePublish )
		{
			compiled = await EditorUtility.Projects.Compile( project, line => compileLog.AppendLine( line ) );
			if ( compiled is null || compiled.Length == 0 )
				throw new Exception( $"'{ident}' produced no compiled code.\n{compileLog}" );

			var ordered = compiled.Select( output => output.Compiler.AssemblyName ).ToList();
			assemblyFiles[".bin/manifest.json"] = JsonSerializer.Serialize(
				ordered, new JsonSerializerOptions { WriteIndented = true } );

			foreach ( var assembly in compiled )
			{
				var name = assembly.Compiler.AssemblyName;
				assemblyFiles[$".bin/{name}.xml"] = assembly.XmlDocumentation;
				assemblyFiles[$".bin/{name}.cll"] = assembly.Archive.Serialize();
				codePackages.AddRange( ReferencedCodePackages( assembly.AssemblyData ) );
			}

			// Only games ship package.base; libraries reference it from the game instead.
			if ( project.Config.Type != "game" )
			{
				assemblyFiles.Remove( ".bin/package.base.xml" );
				assemblyFiles.Remove( ".bin/package.base.cll" );
			}
		}

		var publisher = await ProjectPublisher.FromProject( project )
			?? throw new Exception( $"Could not build a publisher for '{ident}'." );

		// Game settings are read out of the compiled assemblies' ConVar metadata.
		if ( project.Config.Type == "game" && compiled is not null )
			publisher.SetMeta( "GameSettings", publisher.GetGameSettings( compiled ) );

		foreach ( var file in assemblyFiles )
		{
			if ( file.Value is byte[] bytes ) await publisher.AddFile( bytes, file.Key );
			else if ( file.Value is string text ) await publisher.AddFile( text, file.Key );
		}

		foreach ( var codePackage in codePackages.Distinct() )
			await publisher.AddCodePackageReference( codePackage );

		// PrePublish only ASKS the backend which files it already has; it marks those Skip=true and
		// uploads nothing. The bytes go up in a separate pass, which the wizard does in its own
		// FinishAsync rather than inside ProjectPublisher — so driving the publisher directly means
		// replicating it. Omitting this posts a manifest referencing files the backend never received,
		// which is a silent no-op rather than an error.
		await publisher.PrePublish();

		var totalFiles = publisher.TotalFileCount;
		var uploadBytes = publisher.MissingFileSize;

		// Local function so the project's type never has to be named - it is not in scope here by name.
		async Task UploadOne( ProjectPublisher.ProjectFile file )
		{
			if ( file.Contents is not null )
			{
				if ( await project.Package.UploadFile( file.Contents, file.Name, _ => { } ) )
					file.Skip = true;
			}
			else if ( file.AbsolutePath is not null )
			{
				if ( await project.Package.UploadFile( file.AbsolutePath, file.Name, _ => { } ) )
					file.Skip = true;
			}
		}

		var pending = publisher.Files.Where( file => !file.Skip && file.Size > 0 ).ToArray();
		var uploadedFiles = 0;
		var running = new List<Task>();

		foreach ( var file in pending )
		{
			running.Add( UploadOne( file ) );

			// Match the wizard's concurrency rather than opening 238 sockets at once.
			while ( running.Count > 8 )
			{
				await Task.WhenAny( running );
				running.RemoveAll( task => task.IsCompleted );
			}
		}
		await Task.WhenAll( running );

		uploadedFiles = pending.Count( file => file.Skip );

		// The wizard's own success condition, and one that is genuinely checkable: every file must have
		// been accepted. Refuse to post a manifest that references bytes the backend does not hold.
		var failed = publisher.Files.Where( file => !file.Skip && file.Size > 0 ).ToArray();
		if ( failed.Length > 0 )
		{
			var names = string.Join( ", ", failed.Take( 5 ).Select( file => file.Name ) );
			throw new Exception(
				$"{failed.Length} of {pending.Length} file(s) failed to upload for '{ident}', so no revision " +
				$"was published. First failures: {names}" );
		}

		publisher.SetChangeDetails( changeTitle, changeDetail ?? string.Empty );
		await publisher.Publish();

		return new PublishOutcome
		{
			Ident = publisher.TargetPackageIdent,
			ChangeTitle = changeTitle,
			SourcePublish = project.IsSourcePublish(),
			TotalFiles = totalFiles,
			UploadedFiles = uploadedFiles,
			UploadedBytes = uploadBytes,
			Url = $"https://sbox.game/{publisher.TargetPackageIdent.Replace( '.', '/' )}"
		};
	}

	/// <summary>
	/// Cloud asset packages an assembly depends on, read from its <c>Sandbox.Cloud/AssetAttribute</c>
	/// metadata. Mirrors the wizard's PeekAssembly so a published package declares the same code
	/// references the wizard would have declared.
	/// </summary>
	private static IEnumerable<string> ReferencedCodePackages( byte[] assemblyData )
	{
		foreach ( var attribute in AssemblyMetadata.GetCustomAttributes( assemblyData )
			.Where( a => a.AttributeFullName == "Sandbox.Cloud/AssetAttribute" ) )
		{
			var ident = $"{attribute.Arguments[0]}";
			if ( !Package.TryParseIdent( ident, out var parts ) )
			{
				Log.Warning( $"Hexagon publish could not parse code package ident '{ident}'." );
				continue;
			}
			yield return $"{parts.org}.{parts.package}";
		}
	}

	/// <summary>A project open in the editor that could be published.</summary>
	public class ProjectSummary
	{
		/// <summary>The ident publish_package takes, e.g. 'kbj.hexagon'.</summary>
		public string Ident { get; set; } = string.Empty;

		/// <summary>True for a library, which publishes source; false for a game, which publishes compiled assets.</summary>
		public bool SourcePublish { get; set; }

		public bool StandaloneOnly { get; set; }

		public string ConfigPath { get; set; } = string.Empty;
	}

	/// <summary>What a publish actually did.</summary>
	public class PublishOutcome
	{
		public string Ident { get; set; } = string.Empty;
		public string ChangeTitle { get; set; } = string.Empty;
		public bool SourcePublish { get; set; }

		/// <summary>Files in the manifest for this revision.</summary>
		public int TotalFiles { get; set; }

		/// <summary>Files the backend did not already have, so the ones this revision actually uploaded.</summary>
		public int UploadedFiles { get; set; }

		public long UploadedBytes { get; set; }

		public string Url { get; set; } = string.Empty;
	}
}
