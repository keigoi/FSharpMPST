// non-deterministic choice for Async
module Choice
open System

type private SuccessException<'T>(value : 'T) =
    inherit Exception()
    member self.Value = value

let choice (tasks : List<Async<'T> * Threading.CancellationTokenSource>) : Async<'T> =
    let sources = List.map (fun (_,t) -> t) tasks
    let wrap (task, cancel) =
        async {
            let! res = task
            // cancel other waitors
            let othersources = List.filter (fun c -> not (c.Equals cancel)) sources
            List.iter (fun (s:Threading.CancellationTokenSource) -> s.Cancel()) othersources;
            // exit Async.Parallel() by raising Exception
            return! Async.FromContinuations (fun (_, econt, _) -> econt (SuccessException(res)));
        }
    async {
        try
            let! xs = tasks |> List.map wrap |> Async.Parallel
            return (xs.[0])
        with 
        | :? SuccessException<'T> as ex -> 
            return ex.Value
    }
