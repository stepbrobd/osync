module Osync.Ssh

open System.Diagnostics

let private startProcess (startInfo: ProcessStartInfo) =
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.RedirectStandardInput <- true
    Process.Start(startInfo)

let runRemote (host: string) (command: string) : Result<string, string> =
    let psi = ProcessStartInfo("ssh", $"{host} {command}")
    use proc = startProcess psi

    let stdout = proc.StandardOutput.ReadToEnd()
    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    match proc.ExitCode with
    | 0 -> Ok(stdout)
    | _ -> Error(stderr)

let writeRemoteFile (host: string) (remotePath: string) (content: string) : Result<unit, string> =
    let psi = ProcessStartInfo("ssh", $"""{host} "cat > '{remotePath}'" """)
    use proc = startProcess psi

    proc.StandardInput.Write(content)
    proc.StandardInput.Close()

    let stderr = proc.StandardError.ReadToEnd()
    proc.WaitForExit()

    match proc.ExitCode with
    | 0 -> Ok()
    | _ -> Error(stderr)
