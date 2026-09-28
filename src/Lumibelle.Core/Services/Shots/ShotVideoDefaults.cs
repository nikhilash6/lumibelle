using lumibelle.Models;

namespace lumibelle.Services.Shots;

public static class ShotVideoDefaults
{
    public static string Aspect(Shot shot, ProjectInfo project) => shot.AspectOverride ?? project.VideoAspect;
    public static Shot Capture(Shot shot, ProjectInfo project)
    {
        var result = shot.Copy(); result.Aspect = Aspect(shot, project); return result;
    }
}
