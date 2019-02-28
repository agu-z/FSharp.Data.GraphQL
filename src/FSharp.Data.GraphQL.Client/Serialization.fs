/// The MIT License (MIT)
/// Copyright (c) 2016 Bazinga Technologies Inc

module FSharp.Data.GraphQL.Client.Serialization

open System
open FSharp.Data
open Microsoft.FSharp.Reflection
open System.Reflection
open System.Collections
open System.Collections.Generic
open System.Globalization

let private isoDateFormat = "yyyy-MM-dd" 
let private isoDateTimeFormat = "O"
let private isoFormats = [|isoDateTimeFormat; isoDateFormat|]

let private isOption (t : Type) = 
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ option>

let private (|Option|_|) t =
    if isOption t then Some (Option (t.GetGenericArguments().[0]))
    else None

let private (|Array|_|) (t : Type) =
    if t.IsArray then Some (Array (t.GetElementType()))
    else None

let private (|List|_|) (t : Type) =
    if t.IsGenericType
    then
        let gtype = t.GetGenericTypeDefinition()
        if gtype = typedefof<_ list>
        then Some (List (t.GetGenericArguments().[0]))
        else None
    else None

let private (|Seq|_|) (t : Type) =
    if t.IsGenericType
    then
        let gtype = t.GetGenericTypeDefinition()
        if gtype = typedefof<seq<_>>
        then Some (Seq (t.GetGenericArguments().[0]))
        else None
    else None

let private makeOption t (value : obj) =
    let otype = typedefof<_ option>
    let cases = FSharpType.GetUnionCases(otype.MakeGenericType([|t|]))
    match value with
    | null -> FSharpValue.MakeUnion(cases.[0], [||])
    | _ -> FSharpValue.MakeUnion(cases.[1], [|value|])

let private downcastNone<'T> t =
    match t with
    | Option t -> downcast (makeOption t null)
    | _ -> failwithf "Error parsing JSON value: %O is not an option value." t

let private downcastType (t : Type) x = 
    match t with
    | Option t -> downcast (makeOption t (Convert.ChangeType(x, t)))
    | _ -> downcast (Convert.ChangeType(x, t))

let private isType (expected : Type) (t : Type) =
    match t with
    | Option t -> t = expected
    | _ -> t = expected

let private isNumericType (t : Type) =
    [| typeof<decimal>
       typeof<double>
       typeof<single>
       typeof<uint64>
       typeof<int64>
       typeof<uint32>
       typeof<int>
       typeof<uint16>
       typeof<int16>
       typeof<byte>
       typeof<sbyte> |]
    |> Array.exists (fun expected -> isType expected t)

let private isStringType = isType typeof<string>
let private isDateTimeType = isType typeof<DateTime>
let private isDateTimeOffsetType = isType typeof<DateTimeOffset>
let private isGuidType = isType typeof<Guid>
let private isBooleanType = isType typeof<bool>

let private downcastNumber (t : Type) n =
    match t with
    | t when isNumericType t -> downcastType t n
    | _ -> failwithf "Error parsing JSON value: %O is not a numeric type." t

let private downcastString (t : Type) (s : string) =
    match t with
    | t when isStringType t -> downcastType t s
    | t when isDateTimeType t ->
        match DateTime.TryParseExact(s, isoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | (true, d) -> downcastType t d
        | _ -> failwithf "Error parsing JSON value: %O is a date type, but parsing of value \"%s\" failed." t s
    | t when isDateTimeOffsetType t ->
        match DateTimeOffset.TryParseExact(s, isoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | (true, d) -> downcastType t d
        | _ -> failwithf "Error parsing JSON value: %O is a date time offset type, but parsing of value \"%s\" failed." t s
    | t when isGuidType t ->
        match Guid.TryParse(s) with
        | (true, g) -> downcastType t g
        | _ -> failwithf "Error parsing JSON value: %O is a Guid type, but parsing of value \"%s\" failed." t s
    | _ -> failwithf "Error parsing JSON value: %O is not a string type." t

let private downcastBoolean (t : Type) b =
    match t with
    | t when isBooleanType t -> downcastType t b
    | _ -> failwithf "Error parsing JSON value: %O is not a boolean type." t

let private getPropertyValue (t : Type) (converter : Type -> JsonValue -> 'T) (propName, propValue) =
    let propType = t.GetProperty(propName, BindingFlags.Public ||| BindingFlags.Instance).PropertyType
    converter propType propValue

let rec private getArrayValue (t : Type) (converter : Type -> JsonValue -> obj) (items : JsonValue []) =
    let castArray itemType (items : obj []) : obj =
        let arr = Array.CreateInstance(itemType, items.Length)
        items |> Array.iteri (fun ix i -> arr.SetValue(i, ix))
        upcast arr
    let castList itemType (items : obj list) =
        let tlist = typedefof<_ list>.MakeGenericType([|itemType|])
        let empty = 
            let uc = 
                Reflection.FSharpType.GetUnionCases(tlist) 
                |> Seq.filter (fun uc -> uc.Name = "Empty") 
                |> Seq.exactlyOne
            Reflection.FSharpValue.MakeUnion(uc, [||])
        let rec helper items =
            match items with
            | [] -> empty
            | [x] -> Activator.CreateInstance(tlist, [|x; empty|])
            | x :: xs -> Activator.CreateInstance(tlist, [|x; helper xs|])
        helper items
    match t with
    | Option t -> getArrayValue t converter items |> makeOption t
    | Array itype | Seq itype -> items |> Array.map (converter itype) |> castArray itype
    | List itype -> items |> Array.map (converter itype) |> Array.toList |> castList itype
    | _ -> failwithf "Error parsing JSON value: %O is not an array type." t

let rec private convert t parsed : obj =
    match parsed with
    | JsonValue.Null -> downcastNone t
    | JsonValue.String s -> downcastString t s
    | JsonValue.Number n -> downcastNumber t n
    | JsonValue.Float n -> downcastNumber t n
    | JsonValue.Record props -> 
        let vals = props |> Array.map (getPropertyValue t convert >> box)
        Activator.CreateInstance(t, vals) |> downcastType t
    | JsonValue.Array items -> items |> getArrayValue t convert
    | JsonValue.Boolean b -> downcastBoolean t b

let deserializeRecord<'T> (json : string) : 'T =
    let t = typeof<'T>
    downcast (JsonValue.Parse(json) |> convert t)

let (|EnumerableValue|_|) (x : obj) =
    match x with
    | :? IEnumerable as x -> Some (EnumerableValue (Seq.cast<obj> x |> Array.ofSeq))
    | _ -> None

let (|OptionValue|_|) (x : obj) =
    let xtype = x.GetType()
    if isOption xtype
    then
        match FSharpValue.GetUnionFields(x, xtype) with
        | (_, [|value|]) -> Some (OptionValue Some value)
        | _ -> Some (OptionValue None)
    else None

let serialize (x : obj) =
    let rec helper (x : obj) : JsonValue =
        match x with
        | :? byte as x -> JsonValue.Number (decimal x)
        | :? sbyte as x -> JsonValue.Number (decimal x)
        | :? uint16 as x -> JsonValue.Number (decimal x)
        | :? int16 as x -> JsonValue.Number (decimal x)
        | :? int as x -> JsonValue.Number (decimal x)
        | :? uint32 as x -> JsonValue.Number (decimal x)
        | :? int64 as x -> JsonValue.Number (decimal x)
        | :? uint64 as x -> JsonValue.Number (decimal x)
        | :? single as x -> JsonValue.Float (float x)
        | :? double as x -> JsonValue.Float x
        | :? decimal as x -> JsonValue.Float (float x)
        | :? string as x -> JsonValue.String x
        | :? Guid as x -> JsonValue.String (x.ToString())
        | :? DateTime as x when x.Date = x -> JsonValue.String (x.ToString(isoDateFormat))
        | :? DateTime as x -> JsonValue.String (x.ToString(isoDateTimeFormat))
        | :? DateTimeOffset as x -> JsonValue.String (x.ToString(isoDateTimeFormat))
        | :? bool as x -> JsonValue.Boolean x
        | EnumerableValue items -> items |> Array.map helper |> JsonValue.Array
        | null -> JsonValue.Null
        | OptionValue None -> JsonValue.Null
        | OptionValue (Some x) -> helper x
        | _ ->
            let xtype = x.GetType()
            let xprops = xtype.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
            let items = xprops |> Array.map (fun p -> (p.Name, p.GetValue(x) |> helper))
            JsonValue.Record items
    let value = helper x
    value.ToString()

let deserializeDict (json : string) : IDictionary<string, obj> =
    let rec convert parsed : obj =
        match parsed with
        | JsonValue.Null -> null
        | JsonValue.String s -> upcast s
        | JsonValue.Number n -> upcast n
        | JsonValue.Float n -> upcast n
        | JsonValue.Record props -> upcast (props |> Array.map (fun (n, v) -> (n, (convert v))) |> dict)
        | JsonValue.Array items -> upcast (items |> Array.map convert)
        | JsonValue.Boolean b -> upcast b
    match JsonValue.Parse(json) with
    | JsonValue.Record props -> props |> Array.map (fun (n, v) -> (n, (convert v))) |> dict
    | _ -> failwith "The input JSON could not be deserialized to a record type."