using GlassDesk.UI.Controls;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private GuidedTour? _onboardingGuide;

    /// <summary>Exercises real search/paging without launching or changing library items.</summary>
    public GuidedTour BeginOnboardingGuide()
    {
        _onboardingGuide?.Dispose();
        var previousQuery = SearchBox.Text;
        var previousPage = _view.Page;
        _keepOpenWhileModalCount++;
        GuidedTour tour;
        try
        {
            tour = new GuidedTour(this, LauncherGuideRoot,
            [
                new(SearchBox, "先试着找一个应用", "在高亮的搜索框里输入应用名称、拼音或首字母。结果会即时出现。还没有添加应用时，可以先看完引导，再用“添加”导入。", GuideInteraction.Text),
                new(SearchBox, "清空搜索，回到完整页面", "选中搜索文字后删除，就能重新浏览全部页面。没有搜索文字时可以直接下一步；整理或移除图标前也要先清空搜索。", GuideInteraction.ClearText),
                new(NextPage, "认识页面切换", "应用分布在多页时，点击箭头翻页。只有一页或搜索结果不满一页时，箭头会变灰，这是正常的，可以直接继续。", GuideInteraction.Click),
                new(AddAppButton, "更多应用，从这里添加", "这里可以选择添加文件、程序、文件夹或网站。需要导入时再主动点击；教学不会替你选择文件，也不会启动任何应用。点击“完成实操”回到教学。")
            ]);
        }
        catch { _keepOpenWhileModalCount--; throw; }
        _onboardingGuide = tour;
        tour.Closed += (_, _) =>
        {
            if (ReferenceEquals(_onboardingGuide, tour)) _onboardingGuide = null;
            _keepOpenWhileModalCount = Math.Max(0, _keepOpenWhileModalCount - 1);
            if (Dispatcher.HasShutdownStarted || !IsLoaded) return;
            SearchBox.Text = previousQuery;
            _view.MoveToPage(previousPage);
            RenderTiles();
        };
        return tour;
    }
}
