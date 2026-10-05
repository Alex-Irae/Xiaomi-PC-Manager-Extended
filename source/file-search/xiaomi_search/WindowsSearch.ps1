# Purpose: persistent read-only worker for the existing Windows index and Start apps.
#   search: one ranked SQL query (CONTAINSSEMANTIC text+image OR FREETEXT) across all roots.
#   apps:   Start menu applications (name + AppUserModelID), for the search bar.
# Dependencies: Windows PowerShell 5.1 (inbox System.Data OLE DB), Windows Search service.
# Protocol: one JSON request per stdin line, one JSON response per stdout line; EOF exits.
# Command: powershell -NoProfile -File xiaomi_search/WindowsSearch.ps1   (normally owned by windows_search.py)
# No crawl, no index/scope/registry changes, local index only (no cloud providers).
$ErrorActionPreference='Stop'
[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$taskConnection=$null
function Quote([string]$Value){ return $Value.Replace("'","''") }
function Search($Request){
    if(!$Request.text -or $Request.text.Length -gt 2048){throw 'A nonempty query of up to 2048 characters is required.'}
    $scopes=@()
    foreach($root in @($Request.roots)){
        # Remote and mapped-network roots are never sent to the index.
        if($root.StartsWith('\\')){continue}
        if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($root)).DriveType -notin @([IO.DriveType]::Fixed,[IO.DriveType]::Removable)){continue}
        $scopes+="SCOPE='file:"+(Quote $root.Replace('\','/'))+"'"
    }
    if(!$scopes){return @{results=@();semantic=$false;warnings=@()}}
    # Double quotes are phrase syntax inside the predicates; the user text is plain words.
    $phrase=Quote ($Request.text.Replace('"',' '))
    $filter=''
    if($Request.extension){
        if($Request.extension -notmatch '^\.[\w+-]+$'){throw 'Invalid extension filter.'}
        $filter=" AND System.FileExtension='"+$Request.extension+"'"
    }
    $select=" System.ItemPathDisplay,System.Search.Rank,System.Search.AutoSummary FROM SystemIndex WHERE ("+($scopes -join ' OR ')+") AND System.ItemType<>'Directory'"+$filter+" AND "
    # ponytail: LCID 1033 (English word breaking) is fixed; make it a setting if other languages matter.
    # Documents first, then pictures: one mixed statement let image matches outrank text
    # for ordinary queries on a whole-drive scope.
    $text="(CONTAINSSEMANTIC('text', *, '$phrase', 1033) OR FREETEXT(*, '$phrase'))"
    $image="CONTAINSSEMANTIC('image', *, '$phrase', 1033)"
    $attempts=@(@($true,@(@(150,$text),@(50,$image))),@($false,@(,@(200,"FREETEXT(*, '$phrase')"))))
    $warnings=@()
    foreach($attempt in $attempts){
        try{
            $rows=[Collections.Generic.List[object]]::new()
            foreach($part in $attempt[1]){
            if(!$script:taskConnection){
                $script:taskConnection=[Data.OleDb.OleDbConnection]::new("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';")
                $script:taskConnection.Open()
            }
            $command=$script:taskConnection.CreateCommand()
            $command.CommandTimeout=4
            $command.CommandText='SELECT TOP '+$part[0]+$select+$part[1]+' ORDER BY System.Search.Rank DESC'
            $reader=$command.ExecuteReader()
            try{
                while($reader.Read()){
                    $summary=if($reader.IsDBNull(2)){''}else{[string]$reader.GetValue(2)}
                    $rows.Add(@{path=[string]$reader.GetValue(0);rank=$rows.Count+1;score=[int]$reader.GetValue(1);summary=$summary})
                }
            } finally {$reader.Close();$command.Dispose()}
            }
            return @{results=$rows;semantic=$attempt[0];warnings=$warnings}
        } catch {
            # Older builds reject CONTAINSSEMANTIC; a dropped connection is reopened on the next attempt.
            if($script:taskConnection){$script:taskConnection.Dispose();$script:taskConnection=$null}
            if($attempt[0]){$warnings+='Windows semantic predicate unavailable; Windows keyword results only.'}
            else{throw 'The Windows index query failed.'}
        }
    }
}
while($null -ne ($taskLine=[Console]::In.ReadLine())){
    $taskAnswer=@{results=@();warnings=@()}
    try{
        $taskRequest=$taskLine | ConvertFrom-Json
        if($taskRequest.kind -eq 'apps'){
            $taskAnswer=@{apps=@(Get-StartApps | ForEach-Object {@{name=$_.Name;id=$_.AppID}});warnings=@()}
        } else {$taskAnswer=Search $taskRequest}
    } catch {$taskAnswer=@{results=@();warnings=@([string]$_.Exception.Message)}}
    [Console]::Out.WriteLine(($taskAnswer | ConvertTo-Json -Depth 5 -Compress))
    [Console]::Out.Flush()
}
