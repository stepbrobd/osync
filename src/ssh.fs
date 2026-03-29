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

let copyRealmToTemp (host: string option) (remoteDataPath: string) : Result<string, string> =
    let tempPath =
        Path.Combine(Path.GetTempPath(), $"osync-realm-{Path.GetRandomFileName()}")

    match host with
    | None ->
        try
            let src = Path.Combine(remoteDataPath, "client.realm")
            File.Copy(src, tempPath)
            Ok tempPath
        with ex ->
            Error $"Failed to copy realm: {ex.Message}"
    | Some hostname ->
        let remote = shellEscape (Path.Combine(remoteDataPath, "client.realm"))
        let src = $"{hostname}:{remote}"

        let psi =
            ProcessStartInfo("scp", UseShellExecute = false, RedirectStandardError = true)

        psi.ArgumentList.Add(src)
        psi.ArgumentList.Add(tempPath)
        use proc = Process.Start(psi)
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        match proc.ExitCode with
        | 0 -> Ok tempPath
        | _ -> Error $"scp failed: {stderr}"

let rsyncFiles
    (host: string option)
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
                match host with
                | None -> Path.Combine(srcDataPath, "files") + "/"
                | Some hostname ->
                    let remote = shellEscape (Path.Combine(srcDataPath, "files"))
                    $"{hostname}:{remote}/"

            let destFiles = Path.Combine(destDataPath, "files") + "/"

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
