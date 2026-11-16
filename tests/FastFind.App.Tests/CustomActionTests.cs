using System.IO;
using System.Text.Json;
using FastFind.App.Services;
using Xunit;
using Target = FastFind.App.Services.CustomActions.Target;

namespace FastFind.App.Tests;

/// <summary>The user's own right-click entries: when one is offered, and the command line it comes to.</summary>
public sealed class CustomActionTests
{
    private static CustomAction Action(string appliesTo, string arguments = "", string program = "tool.exe", string name = "Do it")
        => new() { Name = name, AppliesTo = appliesTo, Program = program, Arguments = arguments };

    private static Target File(string path) => new(path, false);
    private static Target Folder(string path) => new(path, true);

    private static CustomActions.Launch One(CustomAction action, params Target[] items)
    {
        var launches = CustomActions.Plan(action, items, out string? error);
        Assert.Null(error);
        return Assert.Single(launches);
    }

    // ---- when it is offered ----

    [Theory]
    [InlineData("ps1")]
    [InlineData(".ps1")]
    [InlineData("*.ps1")]
    [InlineData("PS1")]
    [InlineData("psm1; ps1")]
    [InlineData("psm1,ps1")]
    [InlineData("psm1 ps1")]
    [InlineData("*")]
    [InlineData("executables")]
    public void Offered_for_a_file_it_applies_to(string appliesTo)
        => Assert.True(CustomActions.Matches(Action(appliesTo), new[] { File(@"C:\scripts\Deploy.PS1") }));

    [Theory]
    [InlineData("txt")]
    [InlineData("ps")]
    [InlineData("video")]
    [InlineData("folder")]
    [InlineData("")]
    public void Not_offered_for_a_file_it_does_not_apply_to(string appliesTo)
        => Assert.False(CustomActions.Matches(Action(appliesTo), new[] { File(@"C:\scripts\deploy.ps1") }));

    [Theory]
    [InlineData("video")]
    [InlineData("Video")]
    [InlineData("audio; video")]
    public void A_kind_covers_all_its_extensions(string appliesTo)
    {
        var action = Action(appliesTo);
        foreach (string ext in new[] { "mp4", "mkv", "avi", "MOV", "webm" })
            Assert.True(CustomActions.Matches(action, new[] { File($@"D:\films\holiday.{ext}") }), ext);
        Assert.False(CustomActions.Matches(action, new[] { File(@"D:\films\notes.txt") }));
    }

    [Theory]
    [InlineData("image")]
    [InlineData("images")]
    [InlineData("document")]
    public void A_kind_can_be_written_in_the_singular(string appliesTo)
    {
        string path = appliesTo.StartsWith("image") ? @"C:\a\photo.png" : @"C:\a\report.docx";
        Assert.True(CustomActions.Matches(Action(appliesTo), new[] { File(path) }));
    }

    [Fact]
    public void Everything_selected_has_to_fit()
    {
        var action = Action("video");
        Assert.True(CustomActions.Matches(action, new[] { File(@"D:\a.mp4"), File(@"D:\b.mkv") }));
        Assert.False(CustomActions.Matches(action, new[] { File(@"D:\a.mp4"), File(@"D:\b.txt") }));
        Assert.False(CustomActions.Matches(action, new[] { File(@"D:\a.mp4"), Folder(@"D:\more") }));
        Assert.False(CustomActions.Matches(action, Array.Empty<Target>()));
    }

    [Fact]
    public void Folders_are_offered_only_what_asks_for_folders()
    {
        var folder = new[] { Folder(@"C:\work\project.old") }; // a folder's name can look like it has an extension
        Assert.True(CustomActions.Matches(Action("folder"), folder));
        Assert.True(CustomActions.Matches(Action("*; folder"), folder));
        Assert.False(CustomActions.Matches(Action("*"), folder));
        Assert.False(CustomActions.Matches(Action("old"), folder));
        Assert.False(CustomActions.Matches(Action("folder"), new[] { File(@"C:\work\a.txt") }));
    }

    [Fact]
    public void An_unfinished_action_is_never_offered()
    {
        var file = new[] { File(@"C:\a.txt") };
        Assert.False(CustomActions.Matches(Action("*", name: "  "), file));
        Assert.False(CustomActions.Matches(Action("*", program: ""), file));
    }

    [Fact]
    public void Only_the_fitting_actions_are_listed_and_in_the_users_order()
    {
        var actions = new[] { Action("video", name: "Convert"), Action("ps1", name: "Run"), Action("*", name: "Hash") };
        var offered = CustomActions.For(actions, new[] { File(@"D:\clip.mp4") });
        Assert.Equal(new[] { "Convert", "Hash" }, offered.Select(a => a.Name));
    }

    // ---- the command line ----

    [Fact]
    public void A_path_is_quoted_for_you()
    {
        var launch = One(Action("ps1", "-NoExit -File {path}", "pwsh.exe"), File(@"C:\my scripts\deploy.ps1"));
        Assert.Equal("pwsh.exe", launch.Program);
        Assert.Equal(@"-NoExit -File ""C:\my scripts\deploy.ps1""", launch.Arguments);
        Assert.Equal(@"C:\my scripts", launch.WorkingDirectory);
        Assert.False(launch.Elevated);
    }

    [Fact]
    public void Quotes_the_user_wrote_are_left_alone()
    {
        var launch = One(Action("ps1", @"-File ""{path}"" -Name ""{name}"""), File(@"C:\my scripts\deploy.ps1"));
        Assert.Equal(@"-File ""C:\my scripts\deploy.ps1"" -Name ""deploy.ps1""", launch.Arguments);
    }

    [Fact]
    public void An_argument_built_around_a_placeholder_is_quoted_only_when_it_needs_it()
    {
        var action = Action("video", @"-i {path} -o {folder}\{basename}.mp3 --title={name}");
        Assert.Equal(@"-i ""D:\films\clip.mkv"" -o D:\films\clip.mp3 --title=clip.mkv", One(action, File(@"D:\films\clip.mkv")).Arguments);
        Assert.Equal(@"-i ""D:\my films\a clip.mkv"" -o ""D:\my films\a clip.mp3"" ""--title=a clip.mkv""", One(action, File(@"D:\my films\a clip.mkv")).Arguments);
    }

    [Fact]
    public void A_drive_root_does_not_swallow_the_closing_quote()
    {
        // "C:\" would be read by the program as C:" followed by the rest of the line.
        Assert.Equal(@"--in ""C:\\"" --done", One(Action("folder", "--in {path} --done"), Folder(@"C:\")).Arguments);
        Assert.Equal(@"--in ""C:\\"" --done", One(Action("folder", @"--in ""{path}"" --done"), Folder(@"C:\")).Arguments);
        Assert.Equal(@"--in ""C:\\"" --done", One(Action("folder", "--in {folder} --done"), File(@"C:\pagefile.sys")).Arguments);
    }

    [Fact]
    public void Paths_hands_over_everything_in_one_go()
    {
        var action = Action("video", "--queue {paths}");
        var launch = One(action, File(@"D:\a.mp4"), File(@"D:\my films\b.mkv"), File(@"D:\c.avi"));
        Assert.Equal(@"--queue ""D:\a.mp4"" ""D:\my films\b.mkv"" ""D:\c.avi""", launch.Arguments);
        Assert.True(CustomActions.RunsOnce(action));
    }

    [Fact]
    public void No_arguments_means_everything_selected()
    {
        var launch = One(Action("video", "  "), File(@"D:\a.mp4"), File(@"D:\b.mkv"));
        Assert.Equal(@"""D:\a.mp4"" ""D:\b.mkv""", launch.Arguments);
    }

    [Fact]
    public void Otherwise_the_program_is_started_once_for_each_item()
    {
        var action = Action("ps1", "-File {path}");
        var launches = CustomActions.Plan(action, new[] { File(@"C:\a\one.ps1"), File(@"C:\b\two.ps1") }, out string? error);
        Assert.Null(error);
        Assert.Equal(new[] { @"-File ""C:\a\one.ps1""", @"-File ""C:\b\two.ps1""" }, launches.Select(l => l.Arguments));
        Assert.Equal(new[] { @"C:\a", @"C:\b" }, launches.Select(l => l.WorkingDirectory));
        Assert.False(CustomActions.RunsOnce(action));
    }

    [Fact]
    public void Braces_that_are_not_placeholders_are_passed_through()
    {
        var launch = One(Action("ps1", "-Command & {path} -Filter {$_.Name}"), File(@"C:\a\one.ps1"));
        Assert.Equal(@"-Command & ""C:\a\one.ps1"" -Filter {$_.Name}", launch.Arguments);
    }

    [Fact]
    public void A_folder_starts_the_program_inside_itself()
    {
        var launch = One(Action("folder", "{path}", "code"), Folder(@"C:\work\my project"));
        Assert.Equal(@"""C:\work\my project""", launch.Arguments);
        Assert.Equal(@"C:\work\my project", launch.WorkingDirectory);
    }

    [Fact]
    public void Start_in_program_and_administrator_are_carried_over()
    {
        var action = new CustomAction
        {
            Name = "Build", AppliesTo = "*", Program = @"""%SystemRoot%\System32\cmd.exe""", Arguments = "/k echo {name}",
            StartIn = @"{folder}\out", RunAsAdmin = true,
        };
        var launch = One(action, File(@"C:\work\a.txt"));
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe"), launch.Program);
        Assert.Equal(@"C:\work\out", launch.WorkingDirectory);
        Assert.True(launch.Elevated);
    }

    [Fact]
    public void Too_long_a_command_line_is_refused_rather_than_cut_short()
    {
        var many = Enumerable.Range(0, 2000).Select(i => File($@"D:\a folder with a long name\file number {i:D6}.mp4")).ToArray();
        var launches = CustomActions.Plan(Action("video", "{paths}", name: "Convert"), many, out string? error);
        Assert.Empty(launches);
        Assert.Contains("Convert", error);
    }

    // ---- running and saving ----

    [Fact]
    public async Task Starting_runs_the_program_with_the_arguments_in_the_start_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "FastFind custom action " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string source = Path.Combine(folder, "the source.txt");
            System.IO.File.WriteAllText(source, "hello");
            // A relative target, so the copy lands in the right place only if the start folder was honoured.
            var action = Action("txt", "/c copy {path} copied.txt", "cmd.exe");
            var launch = One(action, File(source));
            Assert.Null(CustomActions.Start(launch));
            string copy = Path.Combine(folder, "copied.txt");
            for (int i = 0; i < 200 && !System.IO.File.Exists(copy); i++) await Task.Delay(25);
            Assert.True(System.IO.File.Exists(copy), "the program did not run");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_program_that_is_not_there_is_reported()
    {
        var launch = new CustomActions.Launch(@"C:\no such folder\no-such-program.exe", "", Path.GetTempPath(), false);
        Assert.Contains("not found", CustomActions.Start(launch));
    }

    [Fact]
    public void Actions_survive_being_saved_and_settings_from_before_they_existed_still_load()
    {
        var settings = new Settings();
        settings.CustomActions.Add(new CustomAction { Name = "Run", AppliesTo = "ps1", Program = "pwsh.exe", Arguments = "-File {path}", StartIn = "{folder}", RunAsAdmin = true });
        var loaded = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
        var action = Assert.Single(loaded.CustomActions);
        Assert.Equal(("Run", "ps1", "pwsh.exe", "-File {path}", "{folder}", true),
            (action.Name, action.AppliesTo, action.Program, action.Arguments, action.StartIn, action.RunAsAdmin));

        Assert.Empty(JsonSerializer.Deserialize<Settings>("""{ "Theme": "Dark" }""")!.CustomActions);
    }
}
