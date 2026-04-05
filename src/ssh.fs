module Osync.Ssh

open System.Diagnostics
open System.IO

/// Shell-escape a string for use inside an SSH command string.
/// Only use this for arguments passed to runRemote or writeRemoteFile,
/// NOT for scp/rsync paths (those use ArgumentList, which bypasses the shell).
let shellEscape (s: string) : string = "'" + s.Replace("'", "'\\''") + "'"

let private startProcess (psi: ProcessStartInfo) : Process =
    match Process.Start(psi) with
    | null -> failwith $"Failed to start {psi.FileName}. Is it installed and in PATH?"
    | proc -> proc

let private waitWithTimeout (proc: Process) (timeoutMs: int) (name: string) =
    if not (proc.WaitForExit(timeoutMs)) then
        proc.Kill()
        failwith $"{name} timed out after {timeoutMs / 1000}s"

let runRemote (host: string) (command: string) : Result<string, string> =
    let psi =
        ProcessStartInfo("ssh", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    psi.ArgumentList.Add(host)
    psi.ArgumentList.Add(command)
    use proc = startProcess psi

    let stderrTask = proc.StandardError.ReadToEndAsync()
    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = stderrTask.Result
    waitWithTimeout proc 300_000 "ssh"

    match proc.ExitCode with
    | 0 -> Ok(stdout)
    | _ -> Error(stderr)

let writeRemoteFile (host: string) (remotePath: string) (content: string) : Result<unit, string> =
    let psi =
        ProcessStartInfo("ssh", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true)

    psi.ArgumentList.Add(host)
    psi.ArgumentList.Add($"cat > {shellEscape remotePath}")
    use proc = startProcess psi

    proc.StandardInput.Write(content)
    proc.StandardInput.Close()

    let stderr = proc.StandardError.ReadToEnd()
    waitWithTimeout proc 60_000 "ssh write"

    match proc.ExitCode with
    | 0 -> Ok()
    | _ -> Error(stderr)

let hashToPath (hash: string) : string =
    Path.Combine(hash.Substring(0, 1), hash.Substring(0, 2), hash)

let private scpFile (src: string) (dest: string) : Result<unit, string> =
    let psi =
        ProcessStartInfo("scp", UseShellExecute = false, RedirectStandardError = true)

    psi.ArgumentList.Add(src)
    psi.ArgumentList.Add(dest)
    use proc = startProcess psi
    let stderr = proc.StandardError.ReadToEnd()
    waitWithTimeout proc 300_000 "scp"

    match proc.ExitCode with
    | 0 -> Ok()
    | _ -> Error $"scp failed: {stderr}"

let copyRealmToTemp (host: string option) (realmPath: string) : Result<string, string> =
    let tempPath =
        Path.Combine(Path.GetTempPath(), $"osync-realm-{Path.GetRandomFileName()}")

    match host with
    | None ->
        try
            File.Copy(realmPath, tempPath)
            Ok tempPath
        with ex ->
            Error $"Failed to copy realm: {ex.Message}"
    | Some hostname ->
        match scpFile $"{hostname}:{realmPath}" tempPath with
        | Ok() -> Ok tempPath
        | Error e -> Error e

let scpToRemote (localPath: string) (hostname: string) (remotePath: string) : Result<string, string> =
    match scpFile localPath $"{hostname}:{remotePath}" with
    | Ok() -> Ok remotePath
    | Error e -> Error e

let rsyncFiles
    (srcHost: string option)
    (destHost: string option)
    (srcDataPath: string)
    (destDataPath: string)
    (hashes: string seq)
    : Result<unit, string> =
    if Seq.isEmpty hashes then
        Ok()
    else if srcHost.IsSome && destHost.IsSome then
        Error
            "rsync does not support both source and destination being remote. Run osync on one of the machines instead."
    else
        let listFile =
            Path.Combine(Path.GetTempPath(), $"osync-rsync-{Path.GetRandomFileName()}")

        try
            File.WriteAllLines(listFile, hashes |> Seq.map hashToPath)

            let srcFilesDir = Path.Combine(srcDataPath, "files")
            let destFilesDir = Path.Combine(destDataPath, "files")

            // Use --rsync-path to cd on the remote side so the remote path is
            // just ".". This avoids spaces in remote paths (e.g. macOS
            // "Application Support") which rsync cannot handle portably.
            let (srcFiles, destFiles, extraArgs) =
                match srcHost, destHost with
                | Some hostname, None ->
                    let rsyncPath = $"cd {shellEscape srcFilesDir} && rsync"
                    ($"{hostname}:.", destFilesDir + "/", [ "--rsync-path"; rsyncPath ])
                | None, Some hostname ->
                    let rsyncPath = $"cd {shellEscape destFilesDir} && rsync"
                    (srcFilesDir + "/", $"{hostname}:.", [ "--rsync-path"; rsyncPath ])
                | _ -> (srcFilesDir + "/", destFilesDir + "/", [])

            let psi = ProcessStartInfo("rsync", UseShellExecute = false)

            for arg in
                [ "-a"; "--info=progress2" ]
                @ extraArgs
                @ [ "--files-from"; listFile; srcFiles; destFiles ] do
                psi.ArgumentList.Add(arg)

            use proc = startProcess psi
            proc.WaitForExit() // no timeout — rsync can take hours for large transfers

            match proc.ExitCode with
            | 0 -> Ok()
            | _ -> Error "rsync failed (see output above)"
        finally
            if File.Exists(listFile) then
                File.Delete(listFile)
