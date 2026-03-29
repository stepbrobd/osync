module Osync.Ssh

open System.Diagnostics

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
