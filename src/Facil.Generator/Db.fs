module internal Facil.Db

open System
open System.Collections.Generic
open System.Data
open System.IO
open System.Text.RegularExpressions
open Microsoft.Data.SqlClient
open Microsoft.SqlServer.TransactSql.ScriptDom

type SqlDataReader with
    member this.IsDBNull(columnName: string) =
        this.GetOrdinal(columnName)
        |> this.IsDBNull
        

let adjustSizeForDbType (dbType: SqlDbType) (size: int16) =
    match dbType with
    | SqlDbType.NChar
    | SqlDbType.NText
    | SqlDbType.NVarChar -> size / 2s
    | _ -> size


let getSysTypeIdLookup (conn: SqlConnection) =
    try
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT system_type_id, name FROM sys.types WHERE system_type_id = user_type_id"
        use reader = cmd.ExecuteReader()
        let lookup = ResizeArray()

        while reader.Read() do
            lookup.Add(reader["system_type_id"] |> unbox<byte> |> int, reader["name"] |> unbox<string>)

        lookup |> Map.ofSeq
    with ex ->
        raise <| Exception("Error getting system type IDs", ex)


// Prefix temp table names to ensure no collisions with existing global temp tables
let facilGlobalTempTablePrefix = $"""FACIL_TEMP_{Guid.NewGuid().ToString("N")}_"""

let rewriteLocalTempTablesToGlobalTempTablesWithPrefix (nameOrDefinition: string) =
    Regex.Replace(nameOrDefinition, "(?<!#)#(?=\w)", "##")
    |> fun s -> Regex.Replace(s, "##(?=\w)", $"##{facilGlobalTempTablePrefix}")


let createAndDropTempTables rewrite (tempTables: TempTable list) (conn: SqlConnection) (tran: SqlTransaction option) =
    for tt in tempTables do
        use cmd = conn.CreateCommand()
        tran |> Option.iter (fun t -> cmd.Transaction <- t)

        cmd.CommandText <-
            tt.Source
            |> if rewrite then
                   rewriteLocalTempTablesToGlobalTempTablesWithPrefix
               else
                   id

        cmd.ExecuteNonQuery() |> ignore

    { new IDisposable with
        member _.Dispose() =
            // Drop all temp tables
            for tt in tempTables do
                use cmd = conn.CreateCommand()
                tran |> Option.iter (fun t -> cmd.Transaction <- t)

                cmd.CommandText <-
                    $"DROP TABLE {tt.Name
                                  |> if rewrite then
                                         rewriteLocalTempTablesToGlobalTempTablesWithPrefix
                                     else
                                         id}"

                cmd.ExecuteNonQuery() |> ignore
    }


let getScriptParameters
    (cfg: RuleSet)
    (sysTypeIdLookup: Map<int, string>)
    (tableTypesByUserId: Map<int, TableType>)
    (script: Script)
    (conn: SqlConnection)
    =

    try

        // Prefix temp var names to ensure no collisions
        let facilTempVarPrefix = $"""@FACIL_VARIABLE_{Guid.NewGuid().ToString("N")}_"""

        let rule = RuleSet.getEffectiveScriptRuleFor script.GlobMatchOutput cfg

        let paramsWithFirstUsageOffset = Dictionary()
        let parser = TSql150Parser(true)
        let fragment, errs = parser.Parse(new StringReader(script.Source))

        if errs.Count > 0 then
            let e = errs[0]

            failwith
                $"Parsing script failed with error %i{e.Number} on line %i{e.Line}, colum %i{e.Column}: %s{e.Message}"

        fragment.Accept
            { new TSqlFragmentVisitor() with
                member _.Visit(node: VariableReference) =
                    base.Visit node

                    match paramsWithFirstUsageOffset.TryGetValue node.Name with
                    | true, offset when offset < node.StartOffset -> ()
                    | _ -> paramsWithFirstUsageOffset[node.Name] <- node.StartOffset
            }

        let declaredParams = ResizeArray()
        let parser = TSql150Parser(true)
        let fragment, errs = parser.Parse(new StringReader(script.Source))

        if errs.Count > 0 then
            let e = errs[0]

            failwith
                $"Parsing script failed with error %i{e.Number} on line %i{e.Line}, colum %i{e.Column}: %s{e.Message}"

        fragment.Accept
            { new TSqlFragmentVisitor() with
                member _.Visit(node: DeclareVariableElement) =
                    base.Visit node
                    declaredParams.Add node.VariableName.Value
            }

        let undeclaredParams = set paramsWithFirstUsageOffset.Keys - set declaredParams

        let sourceToUse =
            // Add parameter declarations from config
            (script.Source, undeclaredParams |> Seq.map (fun s -> s.TrimStart '@'))
            ||> Seq.fold (fun source paramName ->
                match rule |> EffectiveScriptRule.getParam paramName with
                | { Type = Some typeDef } ->
                    $"DECLARE @%s{paramName} %s{typeDef} = %s{facilTempVarPrefix}%s{paramName};\n%s{source}"
                | _ -> source
            )
            |> rewriteLocalTempTablesToGlobalTempTablesWithPrefix


        let unusedParamRules =
            (rule |> EffectiveScriptRule.allParamNames |> Set.map (fun s -> "@" + s))
            - (set paramsWithFirstUsageOffset.Keys)

        for paramName in unusedParamRules do
            logWarning
                $"Script '{script.GlobMatchOutput}' has a matching rule with parameter '%s{paramName}' that is not used in the script. Ignoring parameter."


        use _ = createAndDropTempTables true script.TempTables conn None

        use cmd = conn.CreateCommand()
        cmd.CommandText <- "sys.sp_describe_undeclared_parameters"
        cmd.CommandType <- CommandType.StoredProcedure
        cmd.Parameters.AddWithValue("@tsql", sourceToUse) |> ignore
        use reader = cmd.ExecuteReader()
        let parameters = ResizeArray()

        while reader.Read() do

            let paramName =
                reader["name"]
                |> unbox<string>
                |> fun s ->
                    if s.StartsWith(facilTempVarPrefix, StringComparison.Ordinal) then
                        "@" + s.Substring(facilTempVarPrefix.Length)
                    else
                        s

            let typeInfo =
                let userTypeId =
                    if reader.IsDBNull "suggested_user_type_id" then
                        None
                    else
                        reader["suggested_user_type_id"] |> unbox<int> |> Some

                match userTypeId |> Option.bind tableTypesByUserId.TryFind with
                | Some tt ->
                    match rule |> EffectiveScriptRule.getParam (paramName.TrimStart '@') with
                    | { Nullable = Some true } ->
                        logWarning
                            $"The effective rule for script '{script.GlobMatchOutput}' and parameter '@{paramName}' specifies that the parameter is both nullable and a user-defined table type, but table-valued parameters cannot be nullable. Treating the parameter as non-nullable. To remove this warning, ensure that the parameter does not specify or inherit 'nullable: true'"
                    | _ -> ()

                    Table tt
                | None ->
                    reader["suggested_system_type_id"]
                    |> unbox<int>
                    |> fun id ->
                        sysTypeIdLookup.TryFind id
                        |> Option.defaultWith (fun () ->
                            failwith $"Unsupported SQL system type ID '%i{id}' for parameter '%s{paramName}'"
                        )
                    |> fun typeName ->
                        sqlDbTypeMap.TryFind typeName
                        |> Option.defaultWith (fun () ->
                            failwith $"Unsupported SQL type '%s{typeName}' for parameter '%s{paramName}'"
                        )
                    |> Scalar

            parameters.Add(
                {
                    Name = paramName
                    SortKey = paramsWithFirstUsageOffset[paramName]
                    Size =
                        reader["suggested_max_length"]
                        |> unbox<int16>
                        |> adjustSizeForDbType (
                            match typeInfo with
                            | Scalar ti -> ti.SqlDbType
                            | Table _ -> SqlDbType.Structured
                        )
                    Precision = reader["suggested_precision"] |> unbox<byte>
                    Scale = reader["suggested_scale"] |> unbox<byte>
                    FSharpDefaultValueString =
                        match rule |> EffectiveScriptRule.getParam (paramName.TrimStart '@') with
                        | { Nullable = Some true } -> Some "null"
                        | _ -> None
                    TypeInfo = typeInfo
                    IsOutput = reader["suggested_is_output"] |> unbox<bool>
                    IsCursorRef = false
                }
            )

        parameters |> Seq.toList |> List.sortBy (fun p -> p.SortKey)

    with
    | :? SqlException as ex when
        ex.Message.Contains "Procedure or function"
        && ex.Message.Contains "has too many arguments specified"
        ->
        raise
        <| Exception(
            $"Error getting parameters for script {script.GlobMatchOutput}. If you are using EXEC statements, all parameters passed to the procedure/function you execute may need to be declared in the script or Facil config file.",
            ex
        )
    | :? SqlException as ex when ex.Message.StartsWith("Invalid object name '#", StringComparison.Ordinal) ->
        raise
        <| Exception(
            $"Error getting parameters for script {script.GlobMatchOutput}. If you are using temp tables, you may need to define them in the script's `tempTables` array in the Facil config file.",
            ex
        )
    | :? SqlException as ex when ex.Message.Contains "used more than once in the batch being analyzed" ->
        raise
        <| Exception(
            $"Error getting parameters for script {script.GlobMatchOutput}. Parameters that are used more than once must be specified in the Facil config file.",
            ex
        )
    | ex ->
        raise
        <| Exception($"Error getting parameters for script {script.GlobMatchOutput}", ex)



let getColumnsFromSpDescribeFirstResultSet
    (cfg: RuleSet)
    (sysTypeIdLookup: Map<int, string>)
    (executable: Choice<StoredProcedure, Script, TempTable>)
    (conn: SqlConnection)
    =

    let tempTablesToCreateAndDrop, rewriteTempTableNames =
        match executable with
        | Choice1Of3 sproc ->
            // Only use local temp tables for procedures; since we can't rewrite the sproc like we can with scripts,
            // don't touch global temp tables. They must exist at build-time.
            sproc.TempTables
            |> List.filter (fun x -> not (x.Name.StartsWith("##", StringComparison.Ordinal))),
            false
        | Choice2Of3 script -> script.TempTables, true
        | Choice3Of3 tempTable -> [ tempTable ], true

    use _ =
        createAndDropTempTables rewriteTempTableNames tempTablesToCreateAndDrop conn None

    use cmd = conn.CreateCommand()
    cmd.CommandText <- "sys.sp_describe_first_result_set"
    cmd.CommandType <- CommandType.StoredProcedure

    match executable with
    | Choice1Of3 sproc ->
        cmd.Parameters.AddWithValue("@tsql", sproc.SchemaName + "." + sproc.Name)
        |> ignore
    | Choice2Of3 script ->
        let rule = RuleSet.getEffectiveScriptRuleFor script.GlobMatchOutput cfg

        let sourceToUse =
            (script.Source, (rule |> EffectiveScriptRule.allParams |> Map.toList))
            ||> List.fold (fun source (paramName, p) ->
                match p.Type with
                | None -> source
                | Some typeDef -> $"DECLARE @{paramName} {typeDef}\n{source}"
            )
            |> rewriteLocalTempTablesToGlobalTempTablesWithPrefix

        cmd.Parameters.AddWithValue("@tsql", sourceToUse) |> ignore
    | Choice3Of3 tt ->
        cmd.Parameters.AddWithValue(
            "@tsql",
            $"SELECT * FROM {tt.Name |> rewriteLocalTempTablesToGlobalTempTablesWithPrefix}"
        )
        |> ignore

    use reader = cmd.ExecuteReader()
    let cols = ResizeArray()
    let allColNames = ResizeArray()

    while reader.Read() do
        let colName =
            if reader.IsDBNull "name" then
                None
            else
                reader["name"]
                |> unbox<string>
                |> Some
                |> Option.filter (not << String.IsNullOrEmpty)

        colName |> Option.iter allColNames.Add

        let shouldSkipCol =
            match colName, executable with
            | Some name, Choice1Of3 sproc ->
                RuleSet.getEffectiveProcedureRuleFor sproc.SchemaName sproc.Name cfg
                |> EffectiveProcedureRule.getColumn name
                |> fun c -> c.Skip
                |> Option.defaultValue false
            | Some name, Choice2Of3 script ->
                RuleSet.getEffectiveScriptRuleFor script.GlobMatchOutput cfg
                |> EffectiveScriptRule.getColumn name
                |> fun c -> c.Skip
                |> Option.defaultValue false
            | None, _
            | _, Choice3Of3 _ -> false

        if not shouldSkipCol then

            let typeInfo =
                reader["system_type_id"]
                |> unbox<int>
                |> fun id ->
                    sysTypeIdLookup.TryFind id
                    |> Option.defaultWith (fun () ->
                        failwith
                            $"""Unsupported SQL system type ID '%i{id}' for column '%s{defaultArg colName "<unnamed column>"}'"""
                    )
                |> fun typeName ->
                    sqlDbTypeMap.TryFind typeName
                    |> Option.defaultWith (fun () ->
                        failwith
                            $"""Unsupported SQL type '%s{typeName}' for column '%s{defaultArg colName "<unnamed column>"}'"""
                    )

            cols.Add {
                OutputColumn.Name = colName
                SortKey = reader["column_ordinal"] |> unbox<int>
                IsNullable = reader["is_nullable"] |> unbox<bool>
                TypeInfo = typeInfo
                Collation =
                    if reader.IsDBNull "collation_name" then
                        None
                    else
                        reader["collation_name"] |> unbox<string> |> Some
            }

    if cols.Count = 0 then
        Seq.toList allColNames, None
    else
        Seq.toList allColNames, Seq.toList cols |> List.sortBy (fun c -> c.SortKey) |> Some


let getColumnsFromQuery
    (cfg: RuleSet)
    (executable: Choice<StoredProcedure, Script, TempTable>)
    connStr
    (conn: SqlConnection)
    =
    let tempTablesToCreateAndDrop, rewriteTempTableNames =
        match executable with
        | Choice1Of3 sproc ->
            // Only use local temp tables for procedures; since we can't rewrite the sproc like we can with scripts,
            // don't touch global temp tables. They must exist at build-time.
            sproc.TempTables
            |> List.filter (fun x -> not (x.Name.StartsWith("##", StringComparison.Ordinal))),
            false
        | Choice2Of3 script -> script.TempTables, true
        | Choice3Of3 tempTable -> [ tempTable ], true

    use _ =
        createAndDropTempTables rewriteTempTableNames tempTablesToCreateAndDrop conn None

    let getCmd (conn: SqlConnection) =

        let cmd = conn.CreateCommand()

        match executable with

        | Choice1Of3 sproc ->
            cmd.CommandText <- sproc.SchemaName + "." + sproc.Name
            cmd.CommandType <- CommandType.StoredProcedure
            let rule = RuleSet.getEffectiveProcedureRuleFor sproc.SchemaName sproc.Name cfg

            for param in sproc.Parameters do
                match param.TypeInfo with
                | Scalar ti ->
                    let p = cmd.Parameters.Add(param.Name, ti.SqlDbType)

                    rule
                    |> EffectiveProcedureRule.getParam (param.Name.TrimStart '@')
                    |> fun p -> p.BuildValue
                    |> Option.map box
                    |> Option.defaultValue ti.DefaultBuildValue
                    |> fun v -> p.Value <- if isNull v then box DBNull.Value else v
                | Table tt ->
                    cmd.Parameters.Add(param.Name, SqlDbType.Structured, TypeName = $"{tt.SchemaName}.{tt.Name}")
                    |> ignore

        | Choice2Of3 script ->
            cmd.CommandText <- script.Source |> rewriteLocalTempTablesToGlobalTempTablesWithPrefix
            let rule = RuleSet.getEffectiveScriptRuleFor script.GlobMatchOutput cfg

            for param in script.Parameters do
                match param.TypeInfo with
                | Scalar ti ->
                    let p = cmd.Parameters.Add(param.Name, ti.SqlDbType)

                    rule
                    |> EffectiveScriptRule.getParam (param.Name.TrimStart '@')
                    |> fun p -> p.BuildValue
                    |> Option.map box
                    |> Option.defaultValue ti.DefaultBuildValue
                    |> fun v -> p.Value <- if isNull v then box DBNull.Value else v
                | Table tt ->
                    cmd.Parameters.Add(param.Name, SqlDbType.Structured, TypeName = $"{tt.SchemaName}.{tt.Name}")
                    |> ignore

        | Choice3Of3 tt ->
            cmd.CommandText <- $"SELECT * FROM {tt.Name |> rewriteLocalTempTablesToGlobalTempTablesWithPrefix}"

        cmd

    let reader, cmd, tran, connToClose, tempTables =
        try
            let cmd = getCmd conn
            // SET FMTONLY ON, may fail with dynamic SQL
            cmd.ExecuteReader(CommandBehavior.SchemaOnly), cmd, null, null, null
        with :? SqlException ->
            // Actually execute query - in case it modifies anything, do this with a new connection
            // and in a transaction that is rolled back
            let newConn = new SqlConnection(connStr)
            newConn.Open()
            let tran = newConn.BeginTransaction()

            let tempTables =
                createAndDropTempTables rewriteTempTableNames tempTablesToCreateAndDrop newConn (Some tran)

            let cmd = getCmd newConn
            cmd.Transaction <- tran
            let reader = cmd.ExecuteReader(CommandBehavior.SingleRow)
            reader, cmd, tran, newConn, tempTables

    use _ = connToClose
    use _ = tran
    use _ = tempTables
    use _ = cmd
    use _ = reader

    let schemas = reader.GetColumnSchema()

    if schemas.Count = 0 then
        [], None
    else
        let cols = ResizeArray()
        let allColNames = ResizeArray()

        for schema in schemas do

            let colName =
                if String.IsNullOrEmpty schema.ColumnName then
                    None
                else
                    Some schema.ColumnName

            colName |> Option.iter allColNames.Add

            let shouldSkipCol =
                match colName, executable with
                | Some name, Choice1Of3 sproc ->
                    RuleSet.getEffectiveProcedureRuleFor sproc.SchemaName sproc.Name cfg
                    |> EffectiveProcedureRule.getColumn name
                    |> fun c -> c.Skip
                    |> Option.defaultValue false
                | Some name, Choice2Of3 script ->
                    RuleSet.getEffectiveScriptRuleFor script.GlobMatchOutput cfg
                    |> EffectiveScriptRule.getColumn name
                    |> fun c -> c.Skip
                    |> Option.defaultValue false
                | None, _
                | _, Choice3Of3 _ -> false

            if not shouldSkipCol then

                let typeInfo =
                    schema.DataTypeName
                    |> fun typeName ->
                        sqlDbTypeMap.TryFind typeName
                        |> Option.defaultWith (fun () ->
                            failwith
                                $"""Unsupported SQL type '%s{typeName}' for column '%s{defaultArg colName "<unnamed column>"}'"""
                        )

                cols.Add {
                    OutputColumn.Name = colName
                    SortKey = schema.ColumnOrdinal.Value
                    IsNullable = schema.AllowDBNull.Value
                    TypeInfo = typeInfo
                    Collation = None
                }

        Seq.toList allColNames, Seq.toList cols |> List.sortBy (fun c -> c.SortKey) |> Some


let getColumns connStr conn cfg sysTypeIdLookup (executable: Choice<StoredProcedure, Script, TempTable>) =
    let executableName =
        match executable with
        | Choice1Of3 sp -> $"stored procedure %s{sp.SchemaName}.%s{sp.Name}"
        | Choice2Of3 s -> $"script {s.GlobMatchOutput}"
        | Choice3Of3 tt -> $"temp table {tt.Name}"

    let facilGeneratedSource =
        match executable with
        | Choice2Of3 s when s.GeneratedByFacil -> Some s.Source
        | _ -> None

    let allColNames, cols =
        try
            try
                getColumnsFromSpDescribeFirstResultSet cfg sysTypeIdLookup executable conn
            with :? SqlException ->
                getColumnsFromQuery cfg executable connStr conn
        with ex ->
            match facilGeneratedSource with
            | None -> raise <| Exception($"Error getting output columns for %s{executableName}", ex)
            | Some source ->
                raise
                <| Exception(
                    $"Error getting output columns for Facil-generated %s{executableName}. Script source:\n\n%s{source}\n",
                    ex
                )

    let allColumnNamesWithRules =
        match executable with
        | Choice1Of3 sproc ->
            RuleSet.getEffectiveProcedureRuleFor sproc.SchemaName sproc.Name cfg
            |> EffectiveProcedureRule.allColumnNames
        | Choice2Of3 script ->
            RuleSet.getEffectiveScriptRuleFor script.GlobMatchOutput cfg
            |> EffectiveScriptRule.allColumnNames
        | Choice3Of3 _ -> Set.empty

    for unmatchedColumn in allColumnNamesWithRules - set allColNames do
        logWarning $"Config contains unmatched rule for column '%s{unmatchedColumn}' in {executableName}"

    cols


let getTableTypes (conn: SqlConnection) =
    try
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "
      SELECT
        sys.table_types.user_type_id AS TableTypeUserTypeId,
        SCHEMA_NAME(sys.table_types.schema_id) AS TableTypeSchemaName,
        sys.table_types.name AS TableTypeName,
        sys.columns.name AS ColumnName,
        sys.columns.column_id AS ColumnId,
        sys.columns.max_length AS ColumnSize,
        sys.columns.precision AS ColumnPrecision,
        sys.columns.scale AS ColumnScale,
        sys.columns.is_nullable AS ColumnIsNullable,
        sys.columns.is_identity AS ColumnIsIdentity,
        sys.columns.is_computed AS ColumnIsComputed,
        sys.columns.collation_name AS CollationName,
        sys.columns.generated_always_type AS GeneratedAlwaysType,
        TYPE_NAME(sys.columns.system_type_id) AS ColumnTypeName
      FROM
        sys.table_types
      INNER JOIN
        sys.columns
          ON sys.columns.object_id = sys.table_types.type_table_object_id
    "

        use reader = cmd.ExecuteReader()
        let tableTypes = ResizeArray()

        while reader.Read() do
            let colName = reader["ColumnName"] |> unbox<string>

            let typeInfo =
                reader["ColumnTypeName"]
                |> unbox<string>
                |> fun typeName ->
                    sqlDbTypeMap.TryFind typeName
                    |> Option.defaultWith (fun () ->
                        failwith $"Unsupported SQL type '%s{typeName}' for column '%s{colName}'"
                    )

            tableTypes.Add {
                UserTypeId = reader["TableTypeUserTypeId"] |> unbox<int>
                SchemaName = reader["TableTypeSchemaName"] |> unbox<string>
                Name = reader["TableTypeName"] |> unbox<string>
                // Merged later
                Columns = [
                    {
                        Name = colName
                        IsNullable = reader["ColumnIsNullable"] |> unbox<bool>
                        IsIdentity = reader["ColumnIsIdentity"] |> unbox<bool>
                        IsComputed = reader["ColumnIsComputed"] |> unbox<bool>
                        IsGeneratedAlways = reader["GeneratedAlwaysType"] |> unbox<byte> |> (<>) 0uy
                        SortKey = reader["ColumnId"] |> unbox<int>
                        Size = reader["ColumnSize"] |> unbox<int16> |> adjustSizeForDbType typeInfo.SqlDbType
                        Precision = reader["ColumnPrecision"] |> unbox<byte>
                        Scale = reader["ColumnScale"] |> unbox<byte>
                        TypeInfo = typeInfo
                        Collation =
                            if reader.IsDBNull "CollationName" then
                                None
                            else
                                reader["CollationName"] |> unbox<string> |> Some
                        ShouldSkipInTableDto = false // not relevant/used
                    }
                ]
            }

        tableTypes
        |> Seq.toList
        |> List.groupBy (fun t -> t.UserTypeId)
        |> List.map (fun (_, ts) -> {
            ts.Head with
                Columns = ts |> List.collect (fun t -> t.Columns) |> List.sortBy (fun c -> c.SortKey)
        })
    with ex ->
        raise <| Exception("Error getting table types", ex)


let getStoredProceduresWithoutResultSetOrTempTables
    cfg
    (tableTypesByUserId: Map<int, TableType>)
    (conn: SqlConnection)
    =

    let getStoredProceduresWithoutParamsOrResultSet () =
        try
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                "
        SELECT
          object_id,
          SCHEMA_NAME(schema_id) AS SchemaName,
          name,
          OBJECT_DEFINITION(object_id) AS [Definition]
        FROM
          sys.objects
        WHERE
          [Type] = 'P'
      "

            use reader = cmd.ExecuteReader()
            let sprocs = ResizeArray()

            while reader.Read() do
                let schemaName = reader["SchemaName"] |> unbox<string>
                let name = reader["name"] |> unbox<string>

                sprocs.Add(
                    {
                        ObjectId = reader["object_id"] |> unbox<int>
                        SchemaName = schemaName
                        Name = name
                        Definition =
                            if reader.IsDBNull "Definition" then
                                failwith
                                    $"Unable to get definition of procedure {schemaName}.{name}. Ensure the current principal has the VIEW DEFINITION permission on the procedure."
                            else
                                reader["Definition"] |> unbox<string>
                        Parameters = [] // Added later
                        TempTables = [] // Added later
                        ResultSet = None // Added later
                    }
                )

            sprocs |> Seq.toList
        with ex ->
            raise <| Exception("Error getting stored procedures", ex)

    let getSprocParamsByObjectId () =
        try
            use cmd = conn.CreateCommand()

            cmd.CommandText <-
                "
        SELECT
          object_id,
          OBJECT_NAME(object_id) AS SprocName,
          name,
          parameter_id,
          user_type_id,
          max_length,
          precision,
          scale,
          is_output,
          is_cursor_ref,
          TYPE_NAME(system_type_id) AS SystemTypeName
        FROM
          sys.parameters
      "

            use reader = cmd.ExecuteReader()
            let parameters = ResizeArray()

            while reader.Read() do

                let paramName = reader["name"] |> unbox<string>
                let sprocName = reader["SprocName"] |> unbox<string>

                let typeInfo =
                    match reader["SystemTypeName"] |> unbox<string> with
                    | "table type" ->
                        let userTypeId = reader["user_type_id"] |> unbox<int>

                        tableTypesByUserId.TryFind userTypeId
                        |> Option.defaultWith (fun () ->
                            failwith
                                $"Unknown user type ID '%i{userTypeId}' for table type parameter '%s{paramName}' in stored procedure '%s{sprocName}'"
                        )
                        |> Table
                    | typeName ->
                        sqlDbTypeMap.TryFind typeName
                        |> Option.defaultWith (fun () ->
                            failwith
                                $"Unsupported SQL type '%s{typeName}' for parameter '%s{paramName}' in stored procedure '%s{sprocName}'"
                        )
                        |> Scalar

                parameters.Add(
                    reader["object_id"] |> unbox<int>,
                    {
                        Name = reader["name"] |> unbox<string>
                        SortKey = reader["parameter_id"] |> unbox<int>
                        Size =
                            reader["max_length"]
                            |> unbox<int16>
                            |> adjustSizeForDbType (
                                match typeInfo with
                                | Scalar ti -> ti.SqlDbType
                                | Table _ -> SqlDbType.Structured
                            )
                        Precision = reader["precision"] |> unbox<byte>
                        Scale = reader["scale"] |> unbox<byte>
                        FSharpDefaultValueString = None // Added later
                        TypeInfo = typeInfo
                        IsOutput = reader["is_output"] |> unbox<bool>
                        IsCursorRef = reader["is_cursor_ref"] |> unbox<bool>
                    }
                )

            parameters
            |> Seq.toList
            |> List.groupBy fst
            |> List.map (fun (k, ps) -> k, ps |> List.map snd |> List.sortBy (fun p -> p.SortKey))
            |> Map.ofList

        with ex ->
            raise <| Exception("Error getting stored procedure parameters", ex)

    let sprocsWithoutParamsOrResultSet = getStoredProceduresWithoutParamsOrResultSet ()

    let sprocParamsByObjectId = getSprocParamsByObjectId ()

    sprocsWithoutParamsOrResultSet
    |> List.filter (fun sp -> RuleSet.shouldIncludeProcedure sp.SchemaName sp.Name cfg)
    // Add parameters
    |> List.map (fun sproc -> {
        sproc with
            Parameters = sprocParamsByObjectId.TryFind sproc.ObjectId |> Option.defaultValue []
    })
    // Add parameter default values
    |> List.map (fun sproc ->
        let paramDefaults = getParameterDefaultValues sproc
        let rule = RuleSet.getEffectiveProcedureRuleFor sproc.SchemaName sproc.Name cfg

        {
            sproc with
                Parameters =
                    sproc.Parameters
                    |> List.map (fun param -> {
                        param with
                            FSharpDefaultValueString =
                                match rule |> EffectiveProcedureRule.getParam (param.Name.TrimStart '@') with
                                | { Nullable = Some true } -> Some "null"
                                | { Nullable = Some false } -> None
                                | { Nullable = None } ->
                                    match paramDefaults.TryGetValue param.Name with
                                    | false, _
                                    | true, None -> None
                                    | true, Some null -> Some "null"
                                    | true, Some x ->
                                        // Note: If, in the future, FSharpDefaultValueString is used for anything other than
                                        // comparing against the literal string "null", keep in mind that using %A will render
                                        // strings with quotes, many number types with suffixes (e.g. 1.0M for decimal), etc.
                                        Some $"%A{x}"
                    })
        }
    )


let getPrimaryKeyColumnNamesByTableName (conn: SqlConnection) =
    try
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "
      SELECT
        schema_name(tab.schema_id) AS [schema_name],
        tab.[name] AS table_name,
        ic.index_column_id AS column_id,
        col.[name] AS column_name
      from sys.tables AS tab
      INNER JOIN
        sys.indexes pk
          ON tab.object_id = pk.object_id
          AND pk.is_primary_key = 1
      INNER JOIN
        sys.index_columns AS ic
          ON ic.object_id = pk.object_id
          AND ic.index_id = pk.index_id
      INNER JOIN
        sys.columns AS col
          ON pk.object_id = col.object_id
          AND col.column_id = ic.column_id
      ORDER BY
        schema_name(tab.schema_id),
        tab.[name],
        ic.index_column_id
    "

        use reader = cmd.ExecuteReader()
        let data = ResizeArray()

        while reader.Read() do

            let rowData =
                reader["schema_name"] |> unbox<string>,
                reader["table_name"] |> unbox<string>,
                reader["column_name"] |> unbox<string>

            data.Add(rowData)

        data
        |> Seq.toList
        |> List.groupBy (fun (schemaName, tableName, _) -> schemaName, tableName)
        |> Map.ofList
        |> Map.map (fun _ rowData -> rowData |> Seq.map (fun (_, _, colName) -> colName) |> Seq.toList)
    with ex ->
        raise <| Exception("Error getting primary key info", ex)


let getTableDtosIncludingThoseNeededForTableScriptsWithSkippedColumns
    cfg
    (sysTypeIdLookup: Map<int, string>)
    (primaryKeyColumnNamesByTable: Map<string * string, string list>)
    (conn: SqlConnection)
    =
    try
        use cmd = conn.CreateCommand()

        cmd.CommandText <-
            "
      SELECT
        SCHEMA_NAME(sys.tables.schema_id) AS SchemaName,
        sys.tables.name AS TableName,
        sys.all_columns.name AS ColName,
        sys.all_columns.column_id,
        sys.all_columns.is_nullable,
        sys.all_columns.system_type_id,
        sys.all_columns.max_length,
        sys.all_columns.precision,
        sys.all_columns.scale,
        sys.all_columns.is_identity,
        sys.all_columns.is_computed,
        sys.all_columns.collation_name,
        sys.all_columns.generated_always_type,
        IsView = CAST(0 AS BIT)
      FROM
        sys.tables
      INNER JOIN
        sys.all_columns
          ON sys.all_columns.object_id = sys.tables.object_id

      UNION

      SELECT
        SCHEMA_NAME(sys.views.schema_id) AS SchemaName,
        sys.views.name AS TableName,
        sys.all_columns.name AS ColName,
        sys.all_columns.column_id,
        sys.all_columns.is_nullable,
        sys.all_columns.system_type_id,
        sys.all_columns.max_length,
        sys.all_columns.precision,
        sys.all_columns.scale,
        sys.all_columns.is_identity,
        sys.all_columns.is_computed,
        sys.all_columns.collation_name,
        sys.all_columns.generated_always_type,
        IsView = CAST(1 AS BIT)
      FROM
        sys.views
      INNER JOIN
        sys.all_columns
          ON sys.all_columns.object_id = sys.views.object_id
    "

        use reader = cmd.ExecuteReader()
        let tableDtos = ResizeArray()
        let allColumnsByTableSchemaAndName = Dictionary<string, ResizeArray<string>>()

        while reader.Read() do

            let schemaName = reader["SchemaName"] |> unbox<string>
            let tableName = reader["TableName"] |> unbox<string>
            let colName = reader["ColName"] |> unbox<string>

            if
                RuleSet.shouldIncludeTableDto schemaName tableName cfg
                || RuleSet.shouldIncludeTableScripts schemaName tableName cfg
            then

                let key = $"{schemaName}.{tableName}"

                match allColumnsByTableSchemaAndName.TryGetValue key with
                | false, _ ->
                    let r = ResizeArray()
                    r.Add colName
                    allColumnsByTableSchemaAndName[key] <- r
                | true, names -> names.Add colName

                let shouldSkipCol =
                    RuleSet.getEffectiveTableDtoRuleFor schemaName tableName cfg
                    |> EffectiveTableDtoRule.getColumn colName
                    |> fun c -> c.Skip
                    |> Option.defaultValue false

                let typeInfo =
                    reader["system_type_id"]
                    |> unbox<byte>
                    |> int
                    |> fun id ->
                        sysTypeIdLookup.TryFind id
                        |> Option.teeNone (fun () ->
                            if RuleSet.shouldIncludeTableDto schemaName tableName cfg && not shouldSkipCol then
                                logWarning
                                    $"Unsupported SQL system type ID '%i{id}' for column '%s{colName}' in table '%s{tableName}'; ignoring column. To silence this warning, configure a table DTO rule that sets column as skipped."
                        )
                    |> Option.bind (fun typeName ->
                        sqlDbTypeMap.TryFind typeName
                        |> Option.teeNone (fun () ->
                            if RuleSet.shouldIncludeTableDto schemaName tableName cfg && not shouldSkipCol then
                                logWarning
                                    $"Unsupported SQL system type '%s{typeName}' for column '%s{colName}' in table '%s{tableName}'; ignoring column. To silence this warning, configure a table DTO rule that sets column as skipped."
                        )
                    )

                match typeInfo with
                | None -> ()
                | Some typeInfo ->
                    tableDtos.Add(
                        {
                            SchemaName = schemaName
                            Name = tableName
                            // Merged later
                            Columns = [
                                {
                                    TableColumn.Name = colName
                                    SortKey = reader["column_id"] |> unbox<int>
                                    IsNullable = reader["is_nullable"] |> unbox<bool>
                                    IsIdentity = reader["is_identity"] |> unbox<bool>
                                    IsComputed = reader["is_computed"] |> unbox<bool>
                                    IsGeneratedAlways = reader["generated_always_type"] |> unbox<byte> |> (<>) 0uy
                                    Size =
                                        reader["max_length"] |> unbox<int16> |> adjustSizeForDbType typeInfo.SqlDbType
                                    Precision = reader["precision"] |> unbox<byte>
                                    Scale = reader["scale"] |> unbox<byte>
                                    TypeInfo = typeInfo
                                    Collation =
                                        if reader.IsDBNull "collation_name" then
                                            None
                                        else
                                            reader["collation_name"] |> unbox<string> |> Some
                                    ShouldSkipInTableDto = shouldSkipCol
                                }
                            ]
                            PrimaryKeyColumns = [] // Set later
                            IsView = reader["IsView"] |> unbox<bool>
                        }
                    )

        tableDtos
        |> Seq.toList
        |> List.groupBy (fun dto -> dto.SchemaName, dto.Name, dto.IsView)
        |> List.map (fun ((schemaName, tableName, isView), xs) ->

            let allColumnNamesWithRules =
                RuleSet.getEffectiveTableDtoRuleFor schemaName tableName cfg
                |> EffectiveTableDtoRule.allColumnNames

            let key = $"{schemaName}.{tableName}"

            for unmatchedColumn in allColumnNamesWithRules - set allColumnsByTableSchemaAndName[key] do
                logWarning
                    $"Config contains unmatched rule for column '%s{unmatchedColumn}' in table {schemaName}.{tableName}"

            let cols =
                xs |> List.collect (fun x -> x.Columns) |> List.sortBy (fun c -> c.SortKey)

            {
                SchemaName = schemaName
                Name = tableName
                Columns = cols
                PrimaryKeyColumns =
                    match primaryKeyColumnNamesByTable.TryFind(schemaName, tableName) with
                    | None -> []
                    | Some colNames ->
                        let foundPrimaryKeyColumns =
                            colNames |> List.choose (fun n -> cols |> List.tryFind (fun c -> c.Name = n))

                        if colNames.Length = foundPrimaryKeyColumns.Length then
                            foundPrimaryKeyColumns
                        else
                            []
                IsView = isView
            }
        )
    with ex ->
        raise <| Exception("Error getting table DTOs", ex)



let getTempTable cfg (sysTypeIdLookup: Map<int, string>) definition connStr (conn: SqlConnection) =
    try
        let mutable name = null
        let parser = TSql150Parser(true)
        let fragment, errs = parser.Parse(new StringReader(definition))

        if errs.Count > 0 then
            let e = errs[0]

            failwith
                $"Parsing temp table definition failed with error %i{e.Number} on line %i{e.Line}, colum %i{e.Column}: %s{e.Message}"

        fragment.Accept
            { new TSqlFragmentVisitor() with
                member _.Visit(node: CreateTableStatement) =
                    if not (isNull name) then
                        failwith "Temp table definition must not contain multiple CREATE TABLE statements"

                    base.Visit node
                    name <- node.SchemaObjectName.BaseIdentifier.Value
            }

        if isNull name then
            failwith "No CREATE TABLE statement was found in temp table definition"

        let tempTableWithoutColumns = {
            Name = name
            Source = definition
            Columns = []
        }

        // Create table so we can query it to get columns
        use cmd = conn.CreateCommand()
        cmd.CommandText <- definition
        cmd.ExecuteNonQuery() |> ignore

        let tempTable = {
            tempTableWithoutColumns with
                Columns =
                    getColumns connStr conn cfg sysTypeIdLookup (Choice3Of3 tempTableWithoutColumns)
                    |> Option.defaultValue []
        }

        // Drop table in case other temp tables use the same name
        use cmd = conn.CreateCommand()
        cmd.CommandText <- $"DROP TABLE {tempTable.Name}"
        cmd.ExecuteNonQuery() |> ignore

        tempTable
    with ex ->
        raise
        <| Exception($"Error getting temp table from the following definition:\n%s{definition}", ex)



let getEverything
    (cfg: RuleSet)
    fullYamlPath
    (scriptsWithoutParamsOrResultSetsOrTempTables: Script list)
    connStr
    (conn: SqlConnection)
    =

    let sysTypeIdLookup = getSysTypeIdLookup conn

    let allTableTypes = getTableTypes conn

    let tableTypesByUserId =
        allTableTypes |> List.map (fun t -> t.UserTypeId, t) |> Map.ofList

    let primaryKeyColumnNamesByTable = getPrimaryKeyColumnNamesByTableName conn

    let allTableDtos =
        getTableDtosIncludingThoseNeededForTableScriptsWithSkippedColumns
            cfg
            sysTypeIdLookup
            primaryKeyColumnNamesByTable
            conn

    let tableDtos =
        allTableDtos
        |> List.filter (fun dto -> RuleSet.shouldIncludeTableDto dto.SchemaName dto.Name cfg)
        |> List.map (fun dto -> {
            dto with
                // Remove skipped columns
                Columns = dto.Columns |> List.filter (fun c -> not c.ShouldSkipInTableDto)
                // If any skipped columns are PKs, set PrimaryKeyColumns to empty list
                PrimaryKeyColumns =
                    let pkColNames = dto.PrimaryKeyColumns |> List.map (fun c -> c.Name) |> set

                    let skippedColNames =
                        dto.Columns
                        |> List.filter (fun c -> c.ShouldSkipInTableDto)
                        |> List.map (fun c -> c.Name)

                    if skippedColNames |> List.exists pkColNames.Contains then
                        []
                    else
                        dto.PrimaryKeyColumns
        })
        |> List.sortBy (fun dto -> dto.SchemaName, dto.Name)

    let tempTablesByDefinition =
        (cfg.Scripts |> List.collect (fun s -> s.TempTables |> Option.defaultValue []))
        @ (cfg.Procedures |> List.collect (fun p -> p.TempTables |> Option.defaultValue []))
        |> List.map (fun rule -> rule.Definition)
        |> List.distinct
        |> List.map (fun definition -> definition, getTempTable cfg sysTypeIdLookup definition connStr conn)
        |> Map.ofList


    // This whole solution with inserting table scripts as normal scripts which are then
    // parsed is fairly hacky, but works
    let tableScripts =
        let toInclude =
            allTableDtos
            |> List.filter (fun dto -> RuleSet.shouldIncludeTableScripts dto.SchemaName dto.Name cfg)

        if toInclude.IsEmpty then
            []
        else
            let getParamNameFromColAndRule (col: TableColumn) (rule: TableScriptColumn) =
                rule.ParamName |> Option.defaultValue (String.firstLower col.Name)

            let parameterFromColAndRule (col, rule) = {
                Name = getParamNameFromColAndRule col rule
                SortKey = col.SortKey
                Size = col.Size
                Precision = col.Precision
                Scale = col.Scale
                FSharpDefaultValueString = if col.IsNullable then Some "null" else None
                TypeInfo = Scalar col.TypeInfo
                IsOutput = false
                IsCursorRef = false
            }

            let getTableTypeColMappingIfCanUse (tt: TableType) (tableCols: TableColumn list) =
                match tt.Columns, tableCols with
                | [ ttCol ], [ tableCol ] ->
                    if
                        {
                            ttCol with
                                Name = ""
                                SortKey = 0
                                IsIdentity = false
                                ShouldSkipInTableDto = false
                        } = {
                                tableCol with
                                    Name = ""
                                    SortKey = 0
                                    IsIdentity = false
                                    ShouldSkipInTableDto = false
                            }
                    then
                        Some [ ttCol.Name, tableCol.Name ]
                    else
                        None
                | ttCols, tableCols when ttCols.Length = tableCols.Length ->
                    let ttColsToCheck =
                        ttCols
                        |> List.map (fun x -> {
                            x with
                                SortKey = 0
                                IsIdentity = false
                        })

                    let tableColsToCheck =
                        tableCols
                        |> List.map (fun x -> {
                            x with
                                SortKey = 0
                                IsIdentity = false
                        })

                    let hasSameColumnsIgnoringOrder =
                        ttColsToCheck
                        |> List.forall (fun ttCol -> tableColsToCheck |> List.contains ttCol)
                        && tableColsToCheck
                           |> List.forall (fun tableCol -> ttColsToCheck |> List.contains tableCol)

                    let ttAndTableCols = List.zip ttCols tableCols

                    if hasSameColumnsIgnoringOrder then
                        ttAndTableCols
                        |> List.map (fun (ttCol, tableCol) -> ttCol.Name, tableCol.Name)
                        |> Some
                    else
                        None
                | _ -> None

            let getTableTypeAndColumnMappingForBatchScript
                (rule: EffectiveTableScriptTypeRule)
                (cols: TableColumn list)
                (scriptName: string)
                =
                match rule.TableType with
                | Some ttName ->
                    let tableType =
                        allTableTypes
                        |> List.tryFind (fun tt -> ttName = $"%s{tt.SchemaName}.%s{tt.Name}")
                        |> Option.defaultWith (fun () ->
                            failwithYamlError
                                fullYamlPath
                                0
                                0
                                $"Unable to find table type %s{ttName} specified in rule for table script %s{scriptName}"
                        )

                    let mapping =
                        getTableTypeColMappingIfCanUse tableType cols
                        |> Option.defaultWith (fun () ->
                            failwithYamlError
                                fullYamlPath
                                0
                                0
                                $"The specified table type %s{ttName} can not be used in table script %s{scriptName}"
                        )

                    tableType, mapping
                | None ->
                    let matching =
                        allTableTypes
                        |> List.choose (fun tt ->
                            getTableTypeColMappingIfCanUse tt cols
                            |> Option.map (fun mapping -> tt, mapping)
                        )

                    match matching with
                    | [] -> failwithError $"Unable to find a suitable table type for table script %s{scriptName}"
                    | [ (tt, mapping) ] -> (tt, mapping)
                    | xs ->
                        failwithYamlError
                            fullYamlPath
                            0
                            0
                            $"""Found multiple suitable table types for table script %s{scriptName}. Specify which to use. The matching types are: %s{xs
                                                                                                                                                      |> List.map (fun (tt, _) -> tt.SchemaName + "." + tt.Name)
                                                                                                                                                      |> String.concat ", "}"""

            let warnInvalidColumns scriptType (dto: TableDto) (rule: EffectiveTableScriptTypeRule) =
                for cols in rule.ColumnsFromAllRules do
                    for colName in Map.keys cols |> Seq.choose id do
                        if not (dto.Columns |> Seq.exists (fun c -> c.Name = colName)) then
                            logYamlWarning
                                fullYamlPath
                                0
                                0
                                $"Effective '%s{scriptType}' table script for table or view %s{dto.SchemaName}.%s{dto.Name} references non-existent column '%s{colName}'"

                for colName in rule.FilterColumns |> Option.defaultValue [] do
                    if not (dto.Columns |> Seq.exists (fun c -> c.Name = colName)) then
                        logYamlWarning
                            fullYamlPath
                            0
                            0
                            $"Effective '%s{scriptType}' table script for table or view %s{dto.SchemaName}.%s{dto.Name} references non-existent column '%s{colName}'"


            toInclude
            |> List.collect (fun dto ->
                let rule =
                    RuleSet.getEffectiveTableScriptRuleFor dto.SchemaName dto.Name fullYamlPath cfg

                [

                    // 'getAll' scripts
                    for rule in rule |> TableScriptRule.rulesFor GetAll do

                        warnInvalidColumns "getAll" dto rule

                        let colsWithRule =
                            dto.Columns
                            |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                        let colsToOutputWithRule =
                            colsWithRule |> List.filter (fun (_, rule) -> rule.Skip <> Some true)

                        {
                            GlobMatchOutput = rule.Name
                            RelativePathSegments =
                                let segmentsWithName =
                                    rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                            NameWithoutExtension = Path.GetFileName rule.Name
                            Source =
                                [
                                    "SELECT"

                                    yield!
                                        colsToOutputWithRule
                                        |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                        |> List.mapAllExceptLast (sprintf "%s,")

                                    "FROM"
                                    $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                ]
                                |> String.concat "\n"
                            Parameters = []
                            ResultSet = None
                            TempTables = []
                            GeneratedByFacil = true
                        }

                    // 'getById' scripts
                    for rule in rule |> TableScriptRule.rulesFor GetById do

                        warnInvalidColumns "getById" dto rule

                        let colsWithRule =
                            dto.Columns
                            |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                        let colsToOutputWithRule =
                            colsWithRule |> List.filter (fun (_, rule) -> rule.Skip <> Some true)

                        let pkColsWithRule =
                            match primaryKeyColumnNamesByTable.TryFind(dto.SchemaName, dto.Name) with
                            | None
                            | Some [] ->
                                failwithError
                                    $"Table or view %s{dto.SchemaName}.%s{dto.Name} has no primary keys and can not be used for a 'getById' table script"
                            | Some colNames ->
                                colNames
                                |> List.map (fun n ->
                                    colsWithRule
                                    |> List.tryFind (fun (c, _) -> c.Name = n)
                                    |> Option.defaultWith (fun () ->
                                        failwithError
                                            $"Unable to find primary key '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                    )
                                )

                        {
                            GlobMatchOutput = rule.Name
                            RelativePathSegments =
                                let segmentsWithName =
                                    rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                            NameWithoutExtension = Path.GetFileName rule.Name
                            Source =
                                [
                                    "SELECT"

                                    yield!
                                        colsToOutputWithRule
                                        |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                        |> List.mapAllExceptLast (sprintf "%s,")

                                    "FROM"
                                    $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                    "WHERE"

                                    yield!
                                        pkColsWithRule
                                        |> List.map (fun (col, rule) ->
                                            $"[%s{col.Name}] = @%s{getParamNameFromColAndRule col rule}"
                                        )
                                        |> List.mapAllExceptFirst (sprintf "AND %s")
                                        |> List.map (sprintf "  %s")
                                ]
                                |> String.concat "\n"
                            Parameters = pkColsWithRule |> List.map parameterFromColAndRule
                            ResultSet = None
                            TempTables = []
                            GeneratedByFacil = true
                        }

                    // 'getByIdBatch' scripts
                    for rule in rule |> TableScriptRule.rulesFor GetByIdBatch do

                        warnInvalidColumns "getByIdBatch" dto rule

                        let colsWithRule =
                            dto.Columns
                            |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                        let colsToOutputWithRule =
                            colsWithRule |> List.filter (fun (_, rule) -> rule.Skip <> Some true)

                        let pkColsWithRule =
                            match primaryKeyColumnNamesByTable.TryFind(dto.SchemaName, dto.Name) with
                            | None
                            | Some [] ->
                                failwithError
                                    $"Table or view %s{dto.SchemaName}.%s{dto.Name} has no primary keys and can not be used for a 'getByIdBatch' table script"
                            | Some colNames ->
                                colNames
                                |> List.map (fun n ->
                                    colsWithRule
                                    |> List.tryFind (fun (c, _) -> c.Name = n)
                                    |> Option.defaultWith (fun () ->
                                        failwithError
                                            $"Unable to find primary key '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                    )
                                )

                        let tableType, columnMapping =
                            getTableTypeAndColumnMappingForBatchScript rule (pkColsWithRule |> List.map fst) rule.Name

                        {
                            GlobMatchOutput = rule.Name
                            RelativePathSegments =
                                let segmentsWithName =
                                    rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                            NameWithoutExtension = Path.GetFileName rule.Name
                            Source =
                                [
                                    "SELECT"

                                    yield!
                                        colsToOutputWithRule
                                        |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                        |> List.mapAllExceptLast (sprintf "%s,")

                                    "FROM"
                                    $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                    "WHERE"
                                    "  EXISTS ("
                                    "    SELECT * FROM @ids ids"
                                    "    WHERE"

                                    yield!
                                        columnMapping
                                        |> List.map (fun (ttColName, tableColName) ->
                                            $"ids.[%s{ttColName}] = [%s{dto.Name}].%s{tableColName}"
                                        )
                                        |> List.mapAllExceptFirst (sprintf "AND %s")
                                        |> List.map (sprintf "      %s")

                                    "  )"

                                ]
                                |> String.concat "\n"
                            Parameters = [
                                {
                                    Name = "ids"
                                    SortKey = 0
                                    Size = 0s
                                    Precision = 0uy
                                    Scale = 0uy
                                    FSharpDefaultValueString = None
                                    TypeInfo = Table tableType
                                    IsOutput = false
                                    IsCursorRef = false
                                }
                            ]
                            ResultSet = None
                            TempTables = []
                            GeneratedByFacil = true
                        }

                    // 'getByColumns' scripts
                    for rule in rule |> TableScriptRule.rulesFor GetByColumns do

                        warnInvalidColumns "getByColumns" dto rule

                        let filterColNames =
                            rule.FilterColumns
                            |> Option.defaultWith (fun () ->
                                failwithYamlError
                                    fullYamlPath
                                    0
                                    0
                                    "Table scripts with type 'getByColumns' must specify 'filterColumns'"
                            )

                        if filterColNames.IsEmpty then
                            failwithYamlError
                                fullYamlPath
                                0
                                0
                                "Table scripts with type 'getByColumns' must specify a non-empty list of 'filterColumns'"

                        let colsWithRule =
                            dto.Columns
                            |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                        let colsToOutputWithRule =
                            colsWithRule |> List.filter (fun (_, rule) -> rule.Skip <> Some true)

                        let filterColsWithRule =
                            filterColNames
                            |> List.map (fun n ->
                                colsWithRule
                                |> List.tryFind (fun (c, _) -> c.Name = n)
                                |> Option.defaultWith (fun () ->
                                    failwithYamlError
                                        fullYamlPath
                                        0
                                        0
                                        $"Unable to find the specified filter column '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                )
                            )
                            // Treat all filter columns as non-nullable
                            |> List.map (fun (c, r) -> { c with IsNullable = false }, r)

                        {
                            GlobMatchOutput = rule.Name
                            RelativePathSegments =
                                let segmentsWithName =
                                    rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                            NameWithoutExtension = Path.GetFileName rule.Name
                            Source =
                                [
                                    "SELECT"

                                    yield!
                                        colsToOutputWithRule
                                        |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                        |> List.mapAllExceptLast (sprintf "%s,")

                                    "FROM"
                                    $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                    "WHERE"

                                    yield!
                                        filterColsWithRule
                                        |> List.map (fun (col, rule) ->
                                            $"[%s{col.Name}] = @%s{getParamNameFromColAndRule col rule}"
                                        )
                                        |> List.mapAllExceptFirst (sprintf "AND %s")
                                        |> List.map (sprintf "  %s")
                                ]
                                |> String.concat "\n"
                            Parameters = filterColsWithRule |> List.map parameterFromColAndRule
                            ResultSet = None
                            TempTables = []
                            GeneratedByFacil = true
                        }

                    // 'getByColumnsBatch' scripts
                    for rule in rule |> TableScriptRule.rulesFor GetByColumnsBatch do

                        warnInvalidColumns "getByColumnsBatch" dto rule

                        let filterColNames =
                            rule.FilterColumns
                            |> Option.defaultWith (fun () ->
                                failwithYamlError
                                    fullYamlPath
                                    0
                                    0
                                    "Table scripts with type 'getByColumnsBatch' must specify 'filterColumns'"
                            )

                        if filterColNames.IsEmpty then
                            failwithYamlError
                                fullYamlPath
                                0
                                0
                                "Table scripts with type 'getByColumnsBatch' must specify a non-empty list of 'filterColumns'"

                        let colsWithRule =
                            dto.Columns
                            |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                        let colsToOutputWithRule =
                            colsWithRule |> List.filter (fun (_, rule) -> rule.Skip <> Some true)

                        let filterColsWithRule =
                            filterColNames
                            |> List.map (fun n ->
                                colsWithRule
                                |> List.tryFind (fun (c, _) -> c.Name = n)
                                |> Option.defaultWith (fun () ->
                                    failwithYamlError
                                        fullYamlPath
                                        0
                                        0
                                        $"Unable to find the specified filter column '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                )
                            )
                            // Treat all filter columns as non-nullable
                            |> List.map (fun (c, r) -> { c with IsNullable = false }, r)

                        let tableType, columnMapping =
                            getTableTypeAndColumnMappingForBatchScript
                                rule
                                (filterColsWithRule |> List.map fst)
                                rule.Name

                        {
                            GlobMatchOutput = rule.Name
                            RelativePathSegments =
                                let segmentsWithName =
                                    rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                            NameWithoutExtension = Path.GetFileName rule.Name
                            Source =
                                [
                                    "SELECT"

                                    yield!
                                        colsToOutputWithRule
                                        |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                        |> List.mapAllExceptLast (sprintf "%s,")

                                    "FROM"
                                    $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                    "WHERE"
                                    "  EXISTS ("
                                    "    SELECT * FROM @ids ids"
                                    "    WHERE"

                                    yield!
                                        columnMapping
                                        |> List.map (fun (ttColName, tableColName) ->
                                            $"ids.[%s{ttColName}] = [%s{dto.Name}].%s{tableColName}"
                                        )
                                        |> List.mapAllExceptFirst (sprintf "AND %s")
                                        |> List.map (sprintf "      %s")

                                    "  )"

                                ]
                                |> String.concat "\n"
                            Parameters = [
                                {
                                    Name = "ids"
                                    SortKey = 0
                                    Size = 0s
                                    Precision = 0uy
                                    Scale = 0uy
                                    FSharpDefaultValueString = None
                                    TypeInfo = Table tableType
                                    IsOutput = false
                                    IsCursorRef = false
                                }
                            ]
                            ResultSet = None
                            TempTables = []
                            GeneratedByFacil = true
                        }

                    // 'insert' scripts
                    if not dto.IsView then
                        for rule in rule |> TableScriptRule.rulesFor Insert do

                            warnInvalidColumns "insert" dto rule

                            let colsWithRule =
                                dto.Columns
                                |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                            let colsToInsertWithRule =
                                colsWithRule
                                |> List.filter (fun (col, rule) ->
                                    match rule.Skip with
                                    | None when col.IsIdentity || col.IsComputed || col.IsGeneratedAlways -> false
                                    | None -> true
                                    | Some skip -> not skip && not col.IsComputed && not col.IsGeneratedAlways
                                )

                            let colsToOutputWithRule =
                                colsWithRule |> List.filter (fun (_, rule) -> rule.Output = Some true)

                            {
                                GlobMatchOutput = rule.Name
                                RelativePathSegments =
                                    let segmentsWithName =
                                        rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                    segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                                NameWithoutExtension = Path.GetFileName rule.Name
                                Source =

                                    [
                                        $"INSERT INTO [%s{dto.SchemaName}].[%s{dto.Name}]"
                                        "("

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        ")"

                                        if not colsToOutputWithRule.IsEmpty then
                                            "OUTPUT"

                                            yield!
                                                colsToOutputWithRule
                                                |> List.map (fun (c, _) -> $"  inserted.[%s{c.Name}]")
                                                |> List.mapAllExceptLast (sprintf "%s,")

                                        "VALUES"
                                        "("

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, rule) -> $"  @%s{getParamNameFromColAndRule c rule}")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        ")"
                                    ]
                                    |> String.concat "\n"
                                Parameters = colsToInsertWithRule |> List.map parameterFromColAndRule
                                ResultSet = None
                                TempTables = []
                                GeneratedByFacil = true
                            }

                    // 'insertBatch' scripts
                    if not dto.IsView then
                        for rule in rule |> TableScriptRule.rulesFor InsertBatch do

                            warnInvalidColumns "insertBatch" dto rule

                            let colsWithRule =
                                dto.Columns
                                |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                            let colsToInsertWithRule =
                                colsWithRule
                                |> List.filter (fun (col, rule) ->
                                    match rule.Skip with
                                    | None when col.IsIdentity || col.IsComputed || col.IsGeneratedAlways -> false
                                    | None -> true
                                    | Some skip -> not skip && not col.IsComputed && not col.IsGeneratedAlways
                                )

                            let colsToOutputWithRule =
                                colsWithRule |> List.filter (fun (_, rule) -> rule.Output = Some true)

                            let tempTableName = "#args"

                            {
                                GlobMatchOutput = rule.Name
                                RelativePathSegments =
                                    let segmentsWithName =
                                        rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                    segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                                NameWithoutExtension = Path.GetFileName rule.Name
                                Source =
                                    [
                                        $"INSERT INTO [%s{dto.SchemaName}].[%s{dto.Name}]"
                                        "("

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        ")"

                                        if not colsToOutputWithRule.IsEmpty then
                                            "OUTPUT"

                                            yield!
                                                colsToOutputWithRule
                                                |> List.map (fun (c, _) -> $"  inserted.[%s{c.Name}]")
                                                |> List.mapAllExceptLast (sprintf "%s,")

                                        "SELECT"

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, _) -> $"  [%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        $"FROM {tempTableName}"
                                    ]
                                    |> String.concat "\n"
                                Parameters = []
                                ResultSet = None
                                TempTables = [
                                    {
                                        Name = tempTableName
                                        Source =
                                            [
                                                $"DROP TABLE IF EXISTS {tempTableName}"
                                                $"CREATE TABLE {tempTableName} ("

                                                yield!
                                                    colsToInsertWithRule
                                                    |> List.map (fun (c, _) ->
                                                        let sqlTypeExpr = c.SqlExpression

                                                        let collateExpr =
                                                            c.Collation |> Option.map (sprintf "COLLATE %s")

                                                        let nullExpr = if c.IsNullable then "NULL" else "NOT NULL"

                                                        [
                                                            $"[{c.Name}]"
                                                            sqlTypeExpr
                                                            yield! Option.toList collateExpr
                                                            nullExpr
                                                        ]
                                                        |> String.concat " "
                                                        |> sprintf "  %s"
                                                    )
                                                    |> List.mapAllExceptLast (sprintf "%s,")

                                                ")"
                                            ]
                                            |> String.concat "\n"
                                        Columns = colsToInsertWithRule |> List.map (fst >> TableColumn.toOutputColumn)
                                    }
                                ]
                                GeneratedByFacil = true
                            }


                    // 'update' scripts
                    if not dto.IsView then
                        for rule in rule |> TableScriptRule.rulesFor Update do

                            warnInvalidColumns "update" dto rule

                            let colsWithRule =
                                dto.Columns
                                |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                            let colsToOutputWithRule =
                                colsWithRule |> List.filter (fun (_, rule) -> rule.Output = Some true)

                            let pkColsWithRule =
                                match primaryKeyColumnNamesByTable.TryFind(dto.SchemaName, dto.Name) with
                                | None
                                | Some [] ->
                                    failwithError
                                        $"Table or view %s{dto.SchemaName}.%s{dto.Name} has no primary keys and can not be used for an 'update' table script"
                                | Some colNames ->
                                    colNames
                                    |> List.map (fun n ->
                                        colsWithRule
                                        |> List.tryFind (fun (c, _) -> c.Name = n)
                                        |> Option.defaultWith (fun () ->
                                            failwithError
                                                $"Unable to find primary key '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                        )
                                    )

                            let colsToUpdateWithRule =
                                colsWithRule
                                |> List.filter (fun (c, rule) ->
                                    let isPkCol = pkColsWithRule |> List.exists (fun (pkc, _) -> c.Name = pkc.Name)

                                    match rule.Skip with
                                    | None when c.IsComputed || c.IsGeneratedAlways -> false
                                    | None -> not isPkCol
                                    | Some skip ->
                                        not skip && not isPkCol && not c.IsComputed && not c.IsGeneratedAlways
                                )

                            {
                                GlobMatchOutput = rule.Name
                                RelativePathSegments =
                                    let segmentsWithName =
                                        rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                    segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                                NameWithoutExtension = Path.GetFileName rule.Name
                                Source =

                                    [
                                        "UPDATE"
                                        $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                        "SET"

                                        yield!
                                            colsToUpdateWithRule
                                            |> List.map (fun (c, rule) ->
                                                $"  [%s{c.Name}] = @%s{getParamNameFromColAndRule c rule}"
                                            )
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        if not colsToOutputWithRule.IsEmpty then
                                            "OUTPUT"

                                            yield!
                                                colsToOutputWithRule
                                                |> List.map (fun (c, _) -> $"  inserted.[%s{c.Name}]")
                                                |> List.mapAllExceptLast (sprintf "%s,")

                                        "WHERE"

                                        yield!
                                            pkColsWithRule
                                            |> List.map (fun (col, rule) ->
                                                $"[%s{col.Name}] = @%s{getParamNameFromColAndRule col rule}"
                                            )
                                            |> List.mapAllExceptFirst (sprintf "AND %s")
                                            |> List.map (sprintf "  %s")
                                    ]
                                    |> String.concat "\n"
                                Parameters = pkColsWithRule @ colsToUpdateWithRule |> List.map parameterFromColAndRule
                                ResultSet = None
                                TempTables = []
                                GeneratedByFacil = true
                            }


                    // 'updateBatch' scripts
                    if not dto.IsView then
                        for rule in rule |> TableScriptRule.rulesFor UpdateBatch do

                            warnInvalidColumns "updateBatch" dto rule

                            let colsWithRule =
                                dto.Columns
                                |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                            let colsToOutputWithRule =
                                colsWithRule |> List.filter (fun (_, rule) -> rule.Output = Some true)

                            let pkColsWithRule =
                                match primaryKeyColumnNamesByTable.TryFind(dto.SchemaName, dto.Name) with
                                | None
                                | Some [] ->
                                    failwithError
                                        $"Table or view %s{dto.SchemaName}.%s{dto.Name} has no primary keys and can not be used for an 'update' table script"
                                | Some colNames ->
                                    colNames
                                    |> List.map (fun n ->
                                        colsWithRule
                                        |> List.tryFind (fun (c, _) -> c.Name = n)
                                        |> Option.defaultWith (fun () ->
                                            failwithError
                                                $"Unable to find primary key '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                        )
                                    )

                            let colsToUpdateWithRule =
                                colsWithRule
                                |> List.filter (fun (c, rule) ->
                                    let isPkCol = pkColsWithRule |> List.exists (fun (pkc, _) -> c.Name = pkc.Name)

                                    match rule.Skip with
                                    | None when c.IsComputed || c.IsGeneratedAlways -> false
                                    | None -> not isPkCol
                                    | Some skip ->
                                        not skip && not isPkCol && not c.IsComputed && not c.IsGeneratedAlways
                                )

                            let tempTableName = "#args"

                            {
                                GlobMatchOutput = rule.Name
                                RelativePathSegments =
                                    let segmentsWithName =
                                        rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                    segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                                NameWithoutExtension = Path.GetFileName rule.Name
                                Source =

                                    [
                                        "UPDATE"
                                        $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                        "SET"

                                        yield!
                                            colsToUpdateWithRule
                                            |> List.map (fun (c, _) -> $"  [%s{c.Name}] = x.[%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        if not colsToOutputWithRule.IsEmpty then
                                            "OUTPUT"

                                            yield!
                                                colsToOutputWithRule
                                                |> List.map (fun (c, _) -> $"  inserted.[%s{c.Name}]")
                                                |> List.mapAllExceptLast (sprintf "%s,")

                                        "FROM"
                                        $"  [%s{dto.SchemaName}].[%s{dto.Name}]"
                                        "INNER JOIN"
                                        $"  {tempTableName} AS x"
                                        "    ON"

                                        yield!
                                            pkColsWithRule
                                            |> List.map (fun (col, _) ->
                                                $"[%s{dto.Name}].[%s{col.Name}] = x.[%s{col.Name}]"
                                            )
                                            |> List.mapAllExceptFirst (sprintf "AND %s")
                                            |> List.map (sprintf "      %s")
                                    ]
                                    |> String.concat "\n"
                                Parameters = []
                                ResultSet = None
                                TempTables = [
                                    let tempTableCols = pkColsWithRule @ colsToUpdateWithRule

                                    {
                                        Name = tempTableName
                                        Source =
                                            [
                                                $"DROP TABLE IF EXISTS {tempTableName}"
                                                $"CREATE TABLE {tempTableName} ("

                                                yield!
                                                    tempTableCols
                                                    |> List.map (fun (c, _) ->
                                                        let sqlTypeExpr = c.SqlExpression

                                                        let collateExpr =
                                                            c.Collation |> Option.map (sprintf "COLLATE %s")

                                                        let nullExpr = if c.IsNullable then "NULL" else "NOT NULL"

                                                        [
                                                            $"[{c.Name}]"
                                                            sqlTypeExpr
                                                            yield! Option.toList collateExpr
                                                            nullExpr
                                                        ]
                                                        |> String.concat " "
                                                        |> sprintf "  %s"
                                                    )
                                                    |> List.map (sprintf "%s,")

                                                let colList =
                                                    pkColsWithRule
                                                    |> List.map (fun (c, _) -> c.Name |> sprintf "[%s]")
                                                    |> String.concat ", "

                                                $"  PRIMARY KEY ({colList})"

                                                ")"
                                            ]
                                            |> String.concat "\n"
                                        Columns = tempTableCols |> List.map (fst >> TableColumn.toOutputColumn)
                                    }
                                ]
                                GeneratedByFacil = true
                            }


                    // 'merge' scripts
                    if not dto.IsView then
                        for rule in rule |> TableScriptRule.rulesFor Merge do

                            warnInvalidColumns "merge" dto rule

                            let colsWithRule =
                                dto.Columns
                                |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                            let colsToOutputWithRule =
                                colsWithRule |> List.filter (fun (_, rule) -> rule.Output = Some true)

                            let pkColsWithRule =
                                match primaryKeyColumnNamesByTable.TryFind(dto.SchemaName, dto.Name) with
                                | None
                                | Some [] ->
                                    failwithError
                                        $"Table or view %s{dto.SchemaName}.%s{dto.Name} has no primary keys and can not be used for a 'merge' table script"
                                | Some colNames ->
                                    colNames
                                    |> List.map (fun n ->
                                        colsWithRule
                                        |> List.tryFind (fun (c, _) -> c.Name = n)
                                        |> Option.defaultWith (fun () ->
                                            failwithError
                                                $"Unable to find primary key '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                        )
                                    )

                            let colsToInsertWithRule =
                                colsWithRule
                                |> List.filter (fun (col, rule) ->
                                    match rule.Skip with
                                    | None when col.IsIdentity || col.IsComputed || col.IsGeneratedAlways -> false
                                    | None -> true
                                    | Some skip -> not skip && not col.IsComputed && not col.IsGeneratedAlways
                                )

                            let colsToUpdateWithRule =
                                colsWithRule
                                |> List.filter (fun (c, rule) ->
                                    let isPkCol = pkColsWithRule |> List.exists (fun (pkc, _) -> c.Name = pkc.Name)

                                    match rule.Skip with
                                    | None when c.IsComputed || c.IsGeneratedAlways -> false
                                    | None -> not isPkCol
                                    | Some skip ->
                                        not skip && not isPkCol && not c.IsComputed && not c.IsGeneratedAlways
                                )

                            let allColsWithRule =
                                colsWithRule
                                |> List.filter (fun (c, _) ->
                                    pkColsWithRule |> List.map fst |> List.contains c
                                    || colsToInsertWithRule |> List.map fst |> List.contains c
                                    || colsToUpdateWithRule |> List.map fst |> List.contains c
                                )

                            {
                                GlobMatchOutput = rule.Name
                                RelativePathSegments =
                                    let segmentsWithName =
                                        rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                    segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                                NameWithoutExtension = Path.GetFileName rule.Name
                                Source =

                                    [
                                        $"""MERGE [%s{dto.SchemaName}].[%s{dto.Name}]{if rule.Holdlock then " WITH (HOLDLOCK)" else ""}"""
                                        "USING"
                                        "("
                                        "  SELECT"

                                        yield!
                                            allColsWithRule
                                            |> List.map (fun (c, rule) ->
                                                $"    [%s{c.Name}] = @%s{getParamNameFromColAndRule c rule}"
                                            )
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        ")"
                                        "AS x"

                                        "ON"

                                        yield!
                                            pkColsWithRule
                                            |> List.map (fun (col, _) ->
                                                $"[%s{dto.Name}].[%s{col.Name}] = x.[%s{col.Name}]"
                                            )
                                            |> List.mapAllExceptFirst (sprintf "AND %s")
                                            |> List.map (sprintf "  %s")

                                        ""
                                        "WHEN MATCHED THEN"
                                        "  UPDATE"
                                        "  SET"

                                        yield!
                                            colsToUpdateWithRule
                                            |> List.map (fun (c, _) -> $"    [%s{c.Name}] = x.[%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        ""
                                        "WHEN NOT MATCHED THEN"
                                        "  INSERT"
                                        "  ("

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, _) -> $"    [%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        "  )"

                                        "  VALUES"
                                        "  ("

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, _) -> $"    x.[%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        "  )"


                                        if not colsToOutputWithRule.IsEmpty then
                                            "OUTPUT"

                                            yield!
                                                colsToOutputWithRule
                                                |> List.map (fun (c, _) -> $"  inserted.[%s{c.Name}]")
                                                |> List.mapAllExceptLast (sprintf "%s,")

                                        ";"

                                    ]
                                    |> String.concat "\n"
                                Parameters = pkColsWithRule @ colsToUpdateWithRule |> List.map parameterFromColAndRule
                                ResultSet = None
                                TempTables = []
                                GeneratedByFacil = true
                            }


                    // 'mergeBatch' scripts
                    if not dto.IsView then
                        for rule in rule |> TableScriptRule.rulesFor MergeBatch do

                            warnInvalidColumns "mergeBatch" dto rule

                            let colsWithRule =
                                dto.Columns
                                |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                            let colsToOutputWithRule =
                                colsWithRule |> List.filter (fun (_, rule) -> rule.Output = Some true)

                            let pkColsWithRule =
                                match primaryKeyColumnNamesByTable.TryFind(dto.SchemaName, dto.Name) with
                                | None
                                | Some [] ->
                                    failwithError
                                        $"Table or view %s{dto.SchemaName}.%s{dto.Name} has no primary keys and can not be used for a 'merge' table script"
                                | Some colNames ->
                                    colNames
                                    |> List.map (fun n ->
                                        colsWithRule
                                        |> List.tryFind (fun (c, _) -> c.Name = n)
                                        |> Option.defaultWith (fun () ->
                                            failwithError
                                                $"Unable to find primary key '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                        )
                                    )

                            let colsToInsertWithRule =
                                colsWithRule
                                |> List.filter (fun (col, rule) ->
                                    match rule.Skip with
                                    | None when col.IsIdentity || col.IsComputed || col.IsGeneratedAlways -> false
                                    | None -> true
                                    | Some skip -> not skip && not col.IsComputed && not col.IsGeneratedAlways
                                )

                            let colsToUpdateWithRule =
                                colsWithRule
                                |> List.filter (fun (c, rule) ->
                                    let isPkCol = pkColsWithRule |> List.exists (fun (pkc, _) -> c.Name = pkc.Name)

                                    match rule.Skip with
                                    | None when c.IsComputed || c.IsGeneratedAlways -> false
                                    | None -> not isPkCol
                                    | Some skip ->
                                        not skip && not isPkCol && not c.IsComputed && not c.IsGeneratedAlways
                                )

                            let allColsWithRule =
                                colsWithRule
                                |> List.filter (fun (c, _) ->
                                    pkColsWithRule |> List.map fst |> List.contains c
                                    || colsToInsertWithRule |> List.map fst |> List.contains c
                                    || colsToUpdateWithRule |> List.map fst |> List.contains c
                                )

                            let tempTableName = "#args"

                            {
                                GlobMatchOutput = rule.Name
                                RelativePathSegments =
                                    let segmentsWithName =
                                        rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                    segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                                NameWithoutExtension = Path.GetFileName rule.Name
                                Source =
                                    [
                                        $"""MERGE [%s{dto.SchemaName}].[%s{dto.Name}]{if rule.Holdlock then " WITH (HOLDLOCK)" else ""}"""
                                        "USING"
                                        $"  {tempTableName}"
                                        "AS x"

                                        "ON"

                                        yield!
                                            pkColsWithRule
                                            |> List.map (fun (col, _) ->
                                                $"[%s{dto.Name}].[%s{col.Name}] = x.[%s{col.Name}]"
                                            )
                                            |> List.mapAllExceptFirst (sprintf "AND %s")
                                            |> List.map (sprintf "  %s")

                                        ""
                                        "WHEN MATCHED THEN"
                                        "  UPDATE"
                                        "  SET"

                                        yield!
                                            colsToUpdateWithRule
                                            |> List.map (fun (c, _) -> $"    [%s{c.Name}] = x.[%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        ""
                                        "WHEN NOT MATCHED THEN"
                                        "  INSERT"
                                        "  ("

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, _) -> $"    [%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        "  )"

                                        "  VALUES"
                                        "  ("

                                        yield!
                                            colsToInsertWithRule
                                            |> List.map (fun (c, _) -> $"    x.[%s{c.Name}]")
                                            |> List.mapAllExceptLast (sprintf "%s,")

                                        "  )"


                                        if not colsToOutputWithRule.IsEmpty then
                                            "OUTPUT"

                                            yield!
                                                colsToOutputWithRule
                                                |> List.map (fun (c, _) -> $"  inserted.[%s{c.Name}]")
                                                |> List.mapAllExceptLast (sprintf "%s,")

                                        ";"

                                    ]
                                    |> String.concat "\n"
                                Parameters = []
                                ResultSet = None
                                TempTables = [
                                    {
                                        Name = tempTableName
                                        Source =
                                            [
                                                $"DROP TABLE IF EXISTS {tempTableName}"
                                                $"CREATE TABLE {tempTableName} ("

                                                yield!
                                                    allColsWithRule
                                                    |> List.map (fun (c, _) ->
                                                        let sqlTypeExpr = c.SqlExpression

                                                        let collateExpr =
                                                            c.Collation |> Option.map (sprintf "COLLATE %s")

                                                        let nullExpr = if c.IsNullable then "NULL" else "NOT NULL"

                                                        [
                                                            $"[{c.Name}]"
                                                            sqlTypeExpr
                                                            yield! Option.toList collateExpr
                                                            nullExpr
                                                        ]
                                                        |> String.concat " "
                                                        |> sprintf "  %s"
                                                    )
                                                    |> List.map (sprintf "%s,")

                                                let colList =
                                                    pkColsWithRule
                                                    |> List.map (fun (c, _) -> c.Name |> sprintf "[%s]")
                                                    |> String.concat ", "

                                                $"  PRIMARY KEY ({colList})"

                                                ")"
                                            ]
                                            |> String.concat "\n"
                                        Columns = allColsWithRule |> List.map (fst >> TableColumn.toOutputColumn)
                                    }
                                ]
                                GeneratedByFacil = true
                            }


                    // 'delete' scripts
                    if not dto.IsView then
                        for rule in rule |> TableScriptRule.rulesFor Delete do

                            warnInvalidColumns "delete" dto rule

                            let colsWithRule =
                                dto.Columns
                                |> List.map (fun col -> col, EffectiveTableScriptTypeRule.getColumn col.Name rule)

                            let colsToOutputWithRule =
                                colsWithRule |> List.filter (fun (_, rule) -> rule.Output = Some true)

                            let pkColsWithRule =
                                match primaryKeyColumnNamesByTable.TryFind(dto.SchemaName, dto.Name) with
                                | None
                                | Some [] ->
                                    failwithError
                                        $"Table or view %s{dto.SchemaName}.%s{dto.Name} has no primary keys and can not be used for a 'delete' table script"
                                | Some colNames ->
                                    colNames
                                    |> List.map (fun n ->
                                        colsWithRule
                                        |> List.tryFind (fun (c, _) -> c.Name = n)
                                        |> Option.defaultWith (fun () ->
                                            failwithError
                                                $"Unable to find primary key '%s{n}' in table or view '%s{dto.SchemaName}.%s{dto.Name}'"
                                        )
                                    )

                            {
                                GlobMatchOutput = rule.Name
                                RelativePathSegments =
                                    let segmentsWithName =
                                        rule.Name.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

                                    segmentsWithName[0 .. segmentsWithName.Length - 2] |> Array.toList
                                NameWithoutExtension = Path.GetFileName rule.Name
                                Source =

                                    [
                                        $"DELETE FROM [%s{dto.SchemaName}].[%s{dto.Name}]"

                                        if not colsToOutputWithRule.IsEmpty then
                                            "OUTPUT"

                                            yield!
                                                colsToOutputWithRule
                                                |> List.map (fun (c, _) -> $"  deleted.[%s{c.Name}]")
                                                |> List.mapAllExceptLast (sprintf "%s,")

                                        "WHERE"

                                        yield!
                                            pkColsWithRule
                                            |> List.map (fun (col, rule) ->
                                                $"[%s{col.Name}] = @%s{getParamNameFromColAndRule col rule}"
                                            )
                                            |> List.mapAllExceptFirst (sprintf "AND %s")
                                            |> List.map (sprintf "  %s")
                                    ]
                                    |> String.concat "\n"
                                Parameters = pkColsWithRule |> List.map parameterFromColAndRule
                                ResultSet = None
                                TempTables = []
                                GeneratedByFacil = true
                            }


                ]
            )


    let scripts =
        scriptsWithoutParamsOrResultSetsOrTempTables @ tableScripts
        |> List.map (fun script ->
            let rule = RuleSet.getEffectiveScriptRuleFor script.GlobMatchOutput cfg

            let tempTables =
                if script.GeneratedByFacil then
                    script.TempTables
                else
                    rule.TempTables |> List.map (fun tt -> tempTablesByDefinition[tt.Definition])

            if
                tempTables
                |> List.countBy (fun tt -> tt.Name)
                |> List.exists (fun (_, count) -> count > 1)
            then
                failwithError
                    $"The rule for script '%s{script.GlobMatchOutput}' contains multiple temp table definitions using the same temp table name. This is not supported."

            let paramNames =
                script.Parameters
                |> List.map (fun p -> p.FSharpParamName |> String.firstLower)
                |> set

            let tempTableNames =
                tempTables |> List.map (fun tt -> tt.FSharpName |> String.firstLower) |> set

            if Set.intersect paramNames tempTableNames |> Set.isEmpty |> not then
                failwithError
                    $"Script '%s{script.GlobMatchOutput}' has a temp table with the same name as a parameter. This is not supported."

            { script with TempTables = tempTables }
        )
        |> List.map (fun script ->
            if script.GeneratedByFacil then
                script
            else
                let parameters =
                    getScriptParameters cfg sysTypeIdLookup tableTypesByUserId script conn

                { script with Parameters = parameters }
        )
        |> List.map (fun script ->
            let resultSet = getColumns connStr conn cfg sysTypeIdLookup (Choice2Of3 script)
            { script with ResultSet = resultSet }
        )
        |> List.filter (fun s ->

            let hasUnsupportedParameter =
                s.Parameters
                |> List.exists (fun p ->
                    if p.IsOutput then
                        logWarning
                            $"Parameter '%s{p.Name}' in script '%s{s.GlobMatchOutput}' is an output parameter, which is not supported. Ignoring script. To remove this warning, make sure this script is not included in any rules."

                        true

                    elif p.IsCursorRef then
                        logWarning
                            $"Parameter '%s{p.Name}' in script '%s{s.GlobMatchOutput}' is a cursor reference, which is not supported. Ignoring script. To remove this warning, make sure this script is not included in any rules."

                        true

                    else
                        false
                )

            let hasUnsupportedResultColumn =
                match s.ResultSet with
                | None -> false
                | Some cols ->
                    match cols |> List.tryFindIndex (fun c -> c.Name.IsNone) with
                    | Some idx when idx > 0 || cols.Length > 1 ->
                        logWarning
                            $"Column #{idx + 1} of {cols.Length} returned by script '%s{s.GlobMatchOutput}' is missing a name. Columns without names are only supported if they are the only column in the result set. Ignoring script. To remove this warning, fix the result set or make sure this script is not included in any rules."

                        true
                    | _ -> false

            let hasDuplicateColumnNames =
                match s.ResultSet with
                | None -> false
                | Some cols ->
                    match
                        cols
                        |> List.choose (fun c -> c.Name)
                        |> List.countBy id
                        |> List.filter (fun (_, c) -> c > 1)
                    with
                    | (name, count) :: _ ->
                        logWarning
                            $"Script '%s{s.GlobMatchOutput}' returns %i{count} columns named '{name}'. Columns names must be unique. Ignoring script. To remove this warning, fix the column names or make sure this script is not included in any rules."

                        true
                    | _ -> false

            not hasUnsupportedParameter
            && not hasUnsupportedResultColumn
            && not hasDuplicateColumnNames
        )
        |> List.sortBy (fun s -> s.GlobMatchOutput)

    let sprocs =
        if cfg.Procedures.IsEmpty then
            []
        else
            getStoredProceduresWithoutResultSetOrTempTables cfg tableTypesByUserId conn
            |> List.map (fun sproc ->
                let rule = RuleSet.getEffectiveProcedureRuleFor sproc.SchemaName sproc.Name cfg

                let tempTables =
                    rule.TempTables |> List.map (fun tt -> tempTablesByDefinition[tt.Definition])

                if
                    tempTables
                    |> List.countBy (fun tt -> tt.Name)
                    |> List.exists (fun (_, count) -> count > 1)
                then
                    failwithError
                        $"The rule for procedure '%s{sproc.SchemaName}.%s{sproc.Name}' contains multiple temp table definitions using the same temp table name. This is not supported."

                let paramNames =
                    sproc.Parameters
                    |> List.map (fun p -> p.FSharpParamName |> String.firstLower)
                    |> set

                let tempTableNames =
                    tempTables |> List.map (fun tt -> tt.FSharpName |> String.firstLower) |> set

                if Set.intersect paramNames tempTableNames |> Set.isEmpty |> not then
                    failwithError
                        $"Procedure '%s{sproc.SchemaName}.%s{sproc.Name}' has a temp table with the same name as a parameter. This is not supported."

                { sproc with TempTables = tempTables }
            )
        |> List.map (fun sproc -> {
            sproc with
                ResultSet = getColumns connStr conn cfg sysTypeIdLookup (Choice1Of3 sproc)
        })
        |> List.filter (fun sp -> RuleSet.shouldIncludeProcedure sp.SchemaName sp.Name cfg)
        |> List.filter (fun sp ->

            let hasUnsupportedParameter =
                sp.Parameters
                |> List.exists (fun p ->
                    if p.IsCursorRef then
                        logWarning
                            $"Parameter '%s{p.Name}' in stored procedure '%s{sp.SchemaName}.%s{sp.Name}' is a cursor reference, which is not supported. Ignoring stored procedure. To remove this warning, remove the parameter from the stored procedure or make the procedure is not included in any rules."

                        true
                    else
                        false
                )

            let hasUnsupportedResultColumn =
                match sp.ResultSet with
                | None -> false
                | Some cols ->
                    match cols |> List.tryFindIndex (fun c -> c.Name.IsNone) with
                    | Some idx when idx > 0 || cols.Length > 1 ->
                        logWarning
                            $"Column #{idx + 1} of {cols.Length} returned by stored procedure '{sp.SchemaName}.{sp.Name}' is missing a name. Columns without names are only supported if they are the only column in the result set. Ignoring stored procedure. To remove this warning, fix the result set or make sure this stored procedure is not included in any rules."

                        true
                    | _ -> false

            let hasDuplicateColumnNames =
                match sp.ResultSet with
                | None -> false
                | Some cols ->
                    match
                        cols
                        |> List.choose (fun c -> c.Name)
                        |> List.countBy id
                        |> List.filter (fun (_, c) -> c > 1)
                    with
                    | (name, count) :: _ ->
                        logWarning
                            $"Stored procedure '{sp.SchemaName}.{sp.Name}' returns %i{count} columns named '{name}'. Columns names must be unique. Ignoring stored procedure. To remove this warning, fix the column names or make sure this stored procedure is not included in any rules."

                        true
                    | _ -> false

            not hasUnsupportedParameter
            && not hasUnsupportedResultColumn
            && not hasDuplicateColumnNames
        )
        |> List.sortBy (fun sp -> sp.SchemaName, sp.Name)

    let usedTableTypes =
        [
            yield! sprocs |> List.collect (fun sp -> sp.Parameters)
            yield! scripts |> List.collect (fun s -> s.Parameters)
        ]
        |> List.choose (fun p ->
            match p.TypeInfo with
            | Table tt -> Some(tt.SchemaName, tt.Name)
            | _ -> None
        )
        |> set

    let tableTypes =
        allTableTypes
        |> List.filter (fun tt -> usedTableTypes |> Set.contains (tt.SchemaName, tt.Name))
        |> List.sortBy (fun tt -> tt.SchemaName, tt.Name)


    for i, rule in Seq.indexed cfg.TableDtos do
        let matchesAnything =
            tableDtos
            |> List.exists (fun dto -> rule |> TableDtoRule.matches dto.SchemaName dto.Name)

        if not matchesAnything then
            let includeForExceptExpr =
                match rule.IncludeOrFor, rule.Except with
                | Include pattern, None -> $"include = '%s{pattern}'"
                | Include pattern, Some except -> $"include = '%s{pattern}' and except = '%s{except}'"
                | For pattern, None -> $"for = '%s{pattern}'"
                | For pattern, Some except -> $"for = '%s{pattern}' and except = '%s{except}'"

            logYamlWarning
                fullYamlPath
                0
                0
                $"Table DTO rule at index %i{i} with %s{includeForExceptExpr} does not match any included table DTOs"


    for i, rule in Seq.indexed cfg.TableTypes do
        let matchesAnything =
            tableTypes
            |> List.exists (fun tt -> rule |> TableTypeRule.matches tt.SchemaName tt.Name)

        if not matchesAnything then
            let includeForExpr =
                match rule.Except with
                | None -> $"for = '%s{rule.For}'"
                | Some except -> $"for = '%s{rule.For}' and except = '%s{except}'"

            logYamlWarning
                fullYamlPath
                0
                0
                $"Table type rule at index %i{i} with %s{includeForExpr} does not match any included table types"


    for i, rule in Seq.indexed cfg.Procedures do
        let matchesAnything =
            sprocs
            |> List.exists (fun sp -> rule |> ProcedureRule.matches sp.SchemaName sp.Name)

        if not matchesAnything then
            let includeForExceptExpr =
                match rule.IncludeOrFor, rule.Except with
                | Include pattern, None -> $"include = '%s{pattern}'"
                | Include pattern, Some except -> $"include = '%s{pattern}' and except = '%s{except}'"
                | For pattern, None -> $"for = '%s{pattern}'"
                | For pattern, Some except -> $"for = '%s{pattern}' and except = '%s{except}'"

            logYamlWarning
                fullYamlPath
                0
                0
                $"Procedure rule at index %i{i} with %s{includeForExceptExpr} does not match any included procedures"


    for i, rule in Seq.indexed cfg.Scripts do
        let matchesAnything =
            scripts |> List.exists (fun s -> rule |> ScriptRule.matches s.GlobMatchOutput)

        if not matchesAnything then
            let includeForExceptExpr =
                match rule.IncludeOrFor, rule.Except with
                | Include pattern, None -> $"include = '%s{pattern}'"
                | Include pattern, Some except -> $"include = '%s{pattern}' and except = '%s{except}'"
                | For pattern, None -> $"for = '%s{pattern}'"
                | For pattern, Some except -> $"for = '%s{pattern}' and except = '%s{except}'"

            logYamlWarning
                fullYamlPath
                0
                0
                $"Script rule at index %i{i} with %s{includeForExceptExpr} does not match any included scripts"


    for i, rule in Seq.indexed cfg.Procedures do
        for paramName in rule.Parameters |> Map.toList |> List.choose fst do
            let hasMatchingProcedureAndParam =
                sprocs
                |> List.exists (fun sp ->
                    rule |> ProcedureRule.matches sp.SchemaName sp.Name
                    && sp.Parameters |> List.exists (fun p -> p.FSharpParamName = paramName)
                )

            if not hasMatchingProcedureAndParam then
                let includeForExceptExpr =
                    match rule.IncludeOrFor, rule.Except with
                    | Include pattern, None -> $"include = '%s{pattern}'"
                    | Include pattern, Some except -> $"include = '%s{pattern}' and except = '%s{except}'"
                    | For pattern, None -> $"for = '%s{pattern}'"
                    | For pattern, Some except -> $"for = '%s{pattern}' and except = '%s{except}'"

                logYamlWarning
                    fullYamlPath
                    0
                    0
                    $"Procedure rule at index %i{i} with %s{includeForExceptExpr} has a rule for parameter '%s{paramName}', but the parameter does not exist in any matching procedures"


    for i, rule in Seq.indexed cfg.Scripts do
        for paramName in rule.Parameters |> Map.toList |> List.choose fst do
            let hasMatchingScriptAndParam =
                scripts
                |> List.exists (fun s ->
                    rule |> ScriptRule.matches s.GlobMatchOutput
                    && s.Parameters |> List.exists (fun p -> p.FSharpParamName = paramName)
                )

            if not hasMatchingScriptAndParam then
                let includeForExceptExpr =
                    match rule.IncludeOrFor, rule.Except with
                    | Include pattern, None -> $"include = '%s{pattern}'"
                    | Include pattern, Some except -> $"include = '%s{pattern}' and except = '%s{except}'"
                    | For pattern, None -> $"for = '%s{pattern}'"
                    | For pattern, Some except -> $"for = '%s{pattern}' and except = '%s{except}'"

                logYamlWarning
                    fullYamlPath
                    0
                    0
                    $"Script rule at index %i{i} with %s{includeForExceptExpr} has a rule for parameter '%s{paramName}', but the parameter does not exist in any matching scripts"


    {
        TableDtos = tableDtos
        TableTypes = tableTypes
        StoredProcedures = sprocs
        Scripts = scripts
    }
