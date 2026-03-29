module Osync.Ssh

open System.Diagnostics
open System.IO

let shellEscape (s: string) : string = "'" + s.Replace("'", "'\\''") + "'"

let runRemote (host: string) (command: string) : Result<string, string> =
    let psi =
        ProcessStartInfo("ssh", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true)

    psi.ArgumentList.Add(host)
    psi.ArgumentList.Add(command)
    use proc = Process.Start(psi)

    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    match proc.ExitCode with
    | 0 -> Ok(stdout)
    | _ -> Error(stderr)

let writeRemoteFile (host: string) (remotePath: string) (content: string) : Result<unit, string> =
    let psi =
        ProcessStartInfo("ssh", UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true)

    psi.ArgumentList.Add(host)
    psi.ArgumentList.Add($"cat > {shellEscape remotePath}")
    use proc = Process.Start(psi)

    proc.StandardInput.Write(content)
    proc.StandardInput.Close()

    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

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
    use proc = Process.Start(psi)
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

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
        let src = $"{hostname}:{shellEscape realmPath}"

        match scpFile src tempPath with
        | Ok() -> Ok tempPath
        | Error e -> Error e

let scpToRemote (localPath: string) (hostname: string) (remotePath: string) : Result<string, string> =
    let dest = $"{hostname}:{shellEscape remotePath}"

    match scpFile localPath dest with
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
    else
        let listFile =
            Path.Combine(Path.GetTempPath(), $"osync-rsync-{Path.GetRandomFileName()}")

        try
            File.WriteAllLines(listFile, hashes |> Seq.map hashToPath)

            let srcFiles =
                match srcHost with
                | None -> Path.Combine(srcDataPath, "files") + "/"
                | Some hostname ->
                    let remote = shellEscape (Path.Combine(srcDataPath, "files"))
                    $"{hostname}:{remote}/"

            let destFiles =
                match destHost with
                | None -> Path.Combine(destDataPath, "files") + "/"
                | Some hostname ->
                    let remote = shellEscape (Path.Combine(destDataPath, "files"))
                    $"{hostname}:{remote}/"

            let psi =
                ProcessStartInfo(
                    "rsync",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                )

            for arg in [ "-av"; "--files-from"; listFile; srcFiles; destFiles ] do
                psi.ArgumentList.Add(arg)

            use proc = Process.Start(psi)
            let _stdout = proc.StandardOutput.ReadToEnd()
            let stderr = proc.StandardError.ReadToEnd()
            proc.WaitForExit()

            match proc.ExitCode with
            | 0 -> Ok()
            | _ -> Error $"rsync failed: {stderr}"
        finally
            if File.Exists(listFile) then
                File.Delete(listFile)
