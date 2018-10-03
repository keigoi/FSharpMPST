module Program
open System
open Choice

type Either<'A,'B> = Left of 'A | Right of 'B
let (@@) f x = f x

type Lens<'v1, 'v2, 's1, 's2> = {
    get : 's1 -> 'v1;
    put : 's1 -> 'v2 -> 's2;
  }

exception RoleNotEnabled

(**
 * Local type constructors with the "sum" operaion. 
 *
 * - 'LABEL is a label type with continuation(s).
 *   (example: Msg<int, Close>, Left<Close> or LeftOrRight<Close, Msg<int, Close>>)
 *)
type SessionType<'A> =
  abstract member Sum : 'A -> 'A

type Branch<'ROLE, 'LABEL> when 'LABEL : not struct = 
  Branch of 'ROLE * Lazy<List<'LABEL>> 
  with 
  interface SessionType<Branch<'ROLE, 'LABEL>> with
    member this.Sum (Branch(_,ys)) =
      let (Branch(r1, xs)) = this in
      let merge_uniq ls1 ls2 =
        List.fold 
          (fun ls l -> 
            if List.exists (fun x -> LanguagePrimitives.PhysicalEquality x l) ls then 
              ls 
            else 
              l::ls) ls1 ls2
      in
      Branch (r1, lazy (merge_uniq (xs.Force ()) (ys.Force())))

type Select<'ROLE, 'LABEL> = 
  Select of 'ROLE * Lazy<'LABEL> 
  with 
  interface SessionType<Select<'ROLE, 'LABEL>> with
    member this.Sum e = 
      if LanguagePrimitives.PhysicalEquality this e then 
        this 
      else 
        raise RoleNotEnabled

type Close = 
  Close 
  with
  interface SessionType<Close> with
    member this.Sum _ = this

let sum_all (xs : List<'A> when 'A :> SessionType<'A>) : 'A  =
  List.fold (fun x y -> x.Sum y) (List.head xs) (List.tail xs)

(*
 * The type for global descriptions.
 * 'T is a sequence of local types e.g. PT3<Select<B, Msg<unit, Close>>, Branch<A, Msg<unit, Close>>, Close>
 *)
type MPST<'T> = MPST of Lazy<List<'T>>

(*
 * The lens for MPST type constructor which does actual mering of local channels
 *)
let mpst (l : Lens<'V1,'V2,'S1,'S2>) : Lens<'V1, 'V2, MPST<'S1>, MPST<'S2>> = {
    get = (fun (MPST xs) -> sum_all (List.map l.get (xs.Force())));
    put = (fun (MPST xs) v' -> MPST (lazy ((List.map (fun x -> l.put x v') (xs.Force())))))
  }


// Labels

type Msg<'A, 'CONT> = 
  Msg of Chan.Chan<'A> * 'CONT

type Left<'CONT> = 
  Left_ of Chan.Chan<bool> * 'CONT

type Right<'CONT> = 
  Right_ of Chan.Chan<bool> * 'CONT

type LeftOrRight<'CONTLEFT,'CONTRIGHT> = 
  | LeftOrRight of Chan.Chan<bool> * 'CONTLEFT * 'CONTRIGHT
  | Left__ of  Chan.Chan<bool> * 'CONTLEFT
  | Right__ of  Chan.Chan<bool> * 'CONTRIGHT

(**
 * Local calculus combinators
 *)
let send_msg : 'R -> 'V -> Select<'R, Msg<'V, 'S>> -> 'S = fun _ v (Select (_, m)) ->
  let (Msg(chan, cont)) = m.Force () in
  Chan.push chan v;
  cont

let send_left_ : 'R -> Select<'R, Left<'S>> -> 'S = fun _ (Select (_, m)) ->
  let (Left_(chan, cont)) = m.Force () in
  Chan.push chan true;
  cont

let send_right_ : 'R -> Select<'R, Right<'S>> -> 'S = fun _ (Select (_, m)) ->
  let (Right_(chan, cont)) = m.Force () in
  Chan.push chan false;
  cont

let send_left : 'R -> Select<'R, LeftOrRight<'S, _>> -> 'S = fun _ (Select (_, m)) ->
  match m.Force() with
  | LeftOrRight(chan, cont, _) ->
    Chan.push chan true;
    cont
  | _ ->
    failwith "impossible: send_left"

let send_right : 'R -> Select<'R, LeftOrRight<_, 'S>> -> 'S = fun _ (Select (_, m)) ->
  match m.Force() with
  | LeftOrRight(chan, _, cont) ->
    Chan.push chan false;
    cont
  | _ ->
    failwith "impossible: send_right"

let receive_msg : 'R -> Branch<'R, Msg<'V, 'S>> -> Async<'V * 'S> = fun _ (Branch (_, ms)) ->
  let wrap (Msg(Chan.Chan(_,cts) as chan, cont)) =
    async {
      let! x = Chan.wait chan
      return (x, cont)
    }, cts
  in
  List.map wrap (ms.Force()) |> Choice.choice

let receive_either : 'R -> Branch<'R, LeftOrRight<'S1, 'S2>> -> Async<Either<'S1, 'S2>> = fun _ (Branch (_, ms)) ->
  let ms = ms.Force() in
  let wrap = function
    | LeftOrRight(Chan.Chan(_,cts) as chan, l, r) ->
      async {
        let! b = Chan.wait chan
        if b then
          return (Left l)
        else
          return (Right r)
      }, cts
    | Left__(Chan.Chan(_,cts) as chan, l) ->
      async {
        let! b = Chan.wait chan
        if b then
          return (Left l)
        else
          return (failwith "impossible: receive_either -- false received for the Left type")
      }, cts
    | Right__(Chan.Chan(_,cts) as chan, r) ->
      async {
        let! b = Chan.wait chan
        if b then
          return (failwith "impossible: receive_either -- true received for the Right type")
        else
          return (Right r)
      }, cts
  in
  ms |> List.map wrap |> Choice.choice
  
let close : Close -> Unit = fun Close -> ()


(**
 * Global type combinators
 *)
type RoleLens<'a, 'b, 'c, 'd, 'r> = Lens<'a, 'b, 'c, 'd>  * 'r

(* The simplest one: Sending of a fixed label "Msg" from role1 to role2 (for readability) *)
let (==>) = fun (lens1, role1) (lens2, role2) cont ->
  let chan = Chan.create () in
  let cont' = lens1.put cont (Select (role2, lazy (Msg(chan, lens1.get cont)))) in
  let cont'' = lens2.put cont' (Branch (role1, lazy [Msg(chan, lens2.get cont')])) in
  cont''

(*
 * Branching between "Left" and "Right"
 *)
let (-%%->) = fun (lens1, role1) (lens2, role2) (((lensL1, _),(lensL2, _)), contLeft) (((lensR1, _),(lensR2, _)), contRight) ->
  let chan = Chan.create () in
  let contLeft' = lensL1.put contLeft Close in
  let contLeft'' = lensL2.put contLeft' Close in
  let contRight' = lensR1.put contRight Close in
  let contRight'' = lensR2.put contRight' Close in
  let sel = Select (role2, lazy LeftOrRight (chan, lensL1.get contLeft, lensR1.get contRight)) in
  let bra = Branch (role1, lazy [LeftOrRight (chan, lensL2.get contLeft', lensR2.get contRight')]) in
  let (MPST contLeft''') = lens2.put (lens1.put contLeft'' sel) bra in
  let (MPST contRight''') = lens2.put (lens1.put contRight'' sel) bra in
  MPST (lazy (contLeft'''.Force() @ contRight'''.Force()))


type A = A
type B = B
type C = C

let get_sess (l,_) m = l.get m

module ThreeParty = begin
  type PT3<'A,'B,'C> = PT3 of 'A * 'B * 'C
  let a = 
    let fst = {get=(fun (PT3(x,_,_)) -> x); put = (fun (PT3(_,y,z)) x' -> PT3(x',y,z))} in
    {get = (fun s -> (mpst fst).get s); put = (fun s v' -> (mpst fst).put s v')}, A
  let b = 
    let snd = {get=(fun (PT3(_,y,_)) -> y); put = (fun (PT3(x,_,z)) y' -> PT3(x,y',z))} in
    {get = (fun s -> (mpst snd).get s); put = (fun s v' -> (mpst snd).put s v')}, B
  let c = 
    let thd = {get=(fun (PT3(_,_,z)) -> z); put = (fun (PT3(x,y,_)) z' -> PT3(x,y,z'))} in
    {get = (fun s -> (mpst thd).get s); put = (fun s v' -> (mpst thd).put s v')}, C
  let finish = MPST (lazy [PT3 (Close, Close, Close)])
end

open ThreeParty

let simple () =
  (a ==> b) @@
  (b ==> c) @@
  (a -%%-> b)
    ((a,b), (b ==> c) @@
            finish)
    ((a,b), (b ==> c) @@
            finish)


(**
 * More labels in global types
 *)
type Label1<'C1, 'C2, 'S, 'R> = 
  {sender : 'C1 -> 'S; receiver : 'C2 -> 'R}

let msg : Unit -> Label1<'C1, 'C2, Msg<'T, 'C1>, Msg<'T, 'C2>> = fun () ->
  let chan = Chan.create () in
  {sender=(fun cont -> Msg(chan, cont));
  receiver=(fun cont -> Msg(chan, cont))}

let left_ : Unit -> Label1<'C1, 'C2, Left<'C1>, LeftOrRight<'C2, 'UNUSED>> = fun () ->
  let chan = Chan.create () in
  {sender=(fun cont -> Left_(chan, cont));
  receiver=(fun cont -> Left__(chan, cont))}
  
let right_ : Unit -> Label1<'C1, 'C2, Right<'C1>, LeftOrRight<'UNUSED, 'C2>> = fun () ->
  let chan = Chan.create () in
  {sender=(fun cont -> Right_(chan, cont));
  receiver=(fun cont -> Right__(chan, cont))}


let (-->) = fun (lens1, role1) (lens2, role2) label cont ->
  let cont' = lens1.put cont (Select (role2, lazy (label.sender (lens1.get cont)))) in
  let cont'' = lens2.put cont' (Branch (role1, lazy [label.receiver (lens2.get cont')])) in
  cont''


(**
 * Example with custom labels
 *)
let create_g () =
    (c --> a) (msg ()) @@
    (a -%%-> b)
      ((a,b),(b --> c) (left_ ()) @@
             (b --> a) (msg ()) @@
             finish)
      ((a,b),(b --> a) (msg ()) @@
             (b --> c) (right_ ()) @@
             (c --> a) (msg ()) @@
             finish)


let t1 s =
  async {
    let! (x, s) = receive_msg C s
    Console.WriteLine "A) received";
    if x = 0 then
      let s = send_left B s in
      let! (str:string, s) = receive_msg B s
      Console.WriteLine("A) B says: {0}", str);
      close s
    else
      let s = send_right B s in
      let! (x:int, s) = receive_msg B s
      let! (str:string, s) = receive_msg C s
      Console.WriteLine("A) B says: {0}, C says: {1}", x, str);
      close s
  }

let t2 s =
  async {
    match! receive_either A s with
    | Left s ->
      Console.WriteLine "B) Left received";
      let s = send_left_ C s in
      let s = send_msg A "Hooray!" s in
      close s
    | Right s ->
      Console.WriteLine "B) Right received";
      let s = send_msg A 1234 s in
      let s = send_right_ C s in
      close s
  }

let t3 s =
  async {
    Console.WriteLine("C) enter a number (0 or others):";)
    let num = Console.In.ReadLine() |> int
    let s = send_msg A num s in
    match! receive_either B s with
    | Left s -> 
      Console.WriteLine "C) Left received";
      close s
    | Right s ->
      Console.WriteLine "C) Right received";
      let s = send_msg A "Hello, A!" s in
      close s
  }

let body () =
  let (sa, sb, sc) =
     let g = create_g () in
     get_sess a g, get_sess b g, get_sess c g
  in
  Async.Parallel [t1 sa; t2 sb; t3 sc] |> Async.RunSynchronously |> ignore


[<EntryPoint>]
let main argv = 
    body ()
    Console.WriteLine("Finished. Press enter key...")
    Console.ReadLine() |> ignore
    0 // return an integer exit code
