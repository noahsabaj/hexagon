public static class HexagonEditorMenu
{
	[Menu( "Editor", "Hexagon/About" )]
	public static void OpenAbout()
	{
		EditorUtility.DisplayDialog( "Hexagon", "Hexagon Roleplay Framework for s&box.\nA spiritual successor to NutScript and Helix." );
	}
}
