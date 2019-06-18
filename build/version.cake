//#addin nuget:?package=Cake.Git&version=0.19.0

#load paths.cake

using System.Text.RegularExpressions;

public static string ReadVersionFromProjectFile(ICakeContext context)
{
    // Expath syntax
    var versionNode = "/Project/PropertyGroup/Version/text()";

    // Extract the value
    return context.XmlPeek(
        Paths.WebProjectFile,
        versionNode,
        new XmlPeekSettings {
            SuppressWarning = true
    });

    // TODO: return the content of the version node in the project file
    //return null;
}
/*
public static bool LatestCommitHasVersionTag(this ICakeContext context)
{
    var latestTag = context.GitDescribe(Paths.RepoDirectory);
    var isVersionTag = Regex.IsMatch(latestTag, @"v[0-9]*");
    var noCommitsAfterLatestTag = !Regex.IsMatch(latestTag, @"\-[0-9]+\-");

    return isVersionTag && noCommitsAfterLatestTag;
}
 */