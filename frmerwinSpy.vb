Friend Class frmERwinSpy
    Inherits System.Windows.Forms.Form
    Private oApplication As SCAPI.Application ' SCAPI Application
    Private oSession As SCAPI.Session ' Active session
    Private oPersistenceUnit As SCAPI.PersistenceUnit ' Active persistence unit

    Private Const PropGridRowName As Integer = 0
    Private Const PropGridRowDataType As Integer = 1
    Private Const PropGridRowValue As Integer = 2
    Private Const PropGridRowAsString As Integer = 3
    Private Const PropGridRowNull As Integer = 4
    Private Const PropGridRowUserDef As Integer = 5
    Private Const PropGridRowVector As Integer = 6
    Private Const PropGridRowTool As Integer = 7
    Private Const PropGridRowReadOnly As Integer = 8
    Private Const PropGridRowDerived As Integer = 9
    Private Const PropGridRowFacetsTrue As Integer = 10
    Private Const PropGridRowFacetsFalse As Integer = 11
    Private Const PropGridRowTotal As Short = 12

    Private Sub frmERwinSpy_Load(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles MyBase.Load
        Try
            oApplication = CreateObject("erwin9.SCAPI")
        Catch ex As Exception
            MsgBox("Failed to launch erwin Spy due to: " + ex.Message)
            Me.Close()
        End Try

        ' Reset UI
        ClearViews()

        ' Populate Models menu
        PopulateModels()

        ' Activae Help
        ActiveHelp("")

        ' Set checkoxes
        mnSpyHelp.Checked = True
        mnSpyHelp_Click(mnSpyHelp, New System.EventArgs()) ' This will reverse the above setting
        mnIntelPropView.Checked = True

        tvObjects.Sorted = True
    End Sub

    Private Sub mnAbout_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnAbout.Click
        Dim aboutForm As frmAbout = New frmAbout()
        aboutForm.Init(oApplication)
        aboutForm.ShowDialog()
        aboutForm = Nothing
    End Sub

    Private Sub mnExit_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnExit.Click
        Me.Close()
    End Sub

    Private Sub frmERwinSpy_FormClosed(ByVal eventSender As System.Object, ByVal eventArgs As System.Windows.Forms.FormClosedEventArgs) Handles Me.FormClosed
        If Not oPersistenceUnit Is Nothing Then
            oSession = Nothing
            oApplication.Sessions.Clear()
            oPersistenceUnit = Nothing
            oApplication = Nothing
        End If
    End Sub

    ' Populate Models menu with available models
    Private Sub PopulateModels()
        Dim oUnit As SCAPI.PersistenceUnit
        Dim nIdx As Integer
        Dim oBag As New SCAPI.PropertyBag
        Dim sTitle As String
        Dim sLocation As String

        ' Reset the menu arrays
        mnModelsArray.DropDownItems.Clear()
        mnModelsArray.Enabled = False

        Do While mnMetaModelsArray.DropDownItems.Count > 1
            mnMetaModelsArray.DropDownItems.RemoveAt(mnMetaModelsArray.DropDownItems.Count - 1)
        Loop

        mnEM2ModelsArray.DropDownItems.Clear()
        mnEM2ModelsArray.Enabled = False

        Do While mnEM2MetaModelsArray.DropDownItems.Count > 1
            mnEM2MetaModelsArray.DropDownItems.RemoveAt(mnEM2MetaModelsArray.DropDownItems.Count - 1)
        Loop

        nIdx = 0
        oPersistenceUnit = Nothing ' Release any active PU

        ' Populate the menu with models
        For Each oUnit In oApplication.PersistenceUnits
            Dim Model As New ToolStripMenuItem
            Dim MetaModel As New ToolStripMenuItem
            Dim EM2Model As New ToolStripMenuItem
            Dim EM2MetaModel As New ToolStripMenuItem
            Model.Tag = nIdx
            MetaModel.Tag = nIdx
            EM2Model.Tag = nIdx
            EM2MetaModel.Tag = nIdx
            ' Get the persistence unit name
            sTitle = oUnit.Name

            ' Populate the bag
            oBag = oUnit.PropertyBag("Locator;Hidden_Model")
            Try
                ' Get the location
                sLocation = oBag.Value("Locator")
                If Len(sLocation) > 0 Then
                    sTitle = sTitle & " (" & sLocation & ")"
                End If
                ' Check if the persistence unit is hidden
                If oBag.Value("Hidden_Model") Then
                    sTitle = sTitle & " [Hidden]"
                End If
                ' Clean up
                oBag.ClearAll()
            Catch ex As Exception

            End Try
            ' Assign a menu item name
            Model.Text = sTitle
            Model.Visible = True
            mnModelsArray.DropDownItems.Add(Model)
            MetaModel.Text = sTitle
            MetaModel.Visible = True
            mnMetaModelsArray.DropDownItems.Add(MetaModel)
            EM2Model.Text = sTitle
            EM2Model.Visible = True
            mnEM2ModelsArray.DropDownItems.Add(EM2Model)
            EM2MetaModel.Text = sTitle
            EM2MetaModel.Visible = True
            mnEM2MetaModelsArray.DropDownItems.Add(EM2MetaModel)
            ' Add the event handler
            AddHandler Model.Click, AddressOf mnModelsArray_Click
            AddHandler MetaModel.Click, AddressOf mnMetaModelsArray_Click
            AddHandler EM2Model.Click, AddressOf mnEM2ModelsArray_Click
            AddHandler EM2MetaModel.Click, AddressOf mnEM2MetaModelsArray_Click
            nIdx = nIdx + 1
        Next oUnit

        If nIdx > 0 Then
            ' Enable menus
            mnModelsArray.Enabled = True
            mnEM2ModelsArray.Enabled = True
        End If
    End Sub
    ' Reset checked items in all menus
    Private Sub ClearMenuChecks()
        Dim item As ToolStripMenuItem
        For Each item In mnModelsArray.DropDownItems
            item.Checked = False
        Next
        For Each item In mnMetaModelsArray.DropDownItems
            item.Checked = False
        Next
        For Each item In mnEM2ModelsArray.DropDownItems
            item.Checked = False
        Next
        For Each item In mnEM2MetaModelsArray.DropDownItems
            item.Checked = False
        Next

        mnIntrinsicMetamodel.Checked = False
        mnEM2IntrinsicMetamodel.Checked = False
    End Sub
    Private Sub mnExtraDebug_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnExtraDebug.Click
        ' Check/Uncheck
        mnExtraDebug.Checked = Not mnExtraDebug.Checked
    End Sub

    ' Open a file
    Private Sub mnFileOpen_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnFileOpen.Click
        Dim oModel As SCAPI.PersistenceUnit
        Dim oBag As New SCAPI.PropertyBag
        Dim nIdx As Short

        ' Set filters.
        ERwinSpyOpenFileDialog.Filter = "All Files (*.*)|*.*|erwin Files (*.erwin)|*.erwin"
        ' Specify default filter.
        ERwinSpyOpenFileDialog.FilterIndex = 2
        ' Current directory
        ERwinSpyOpenFileDialog.InitialDirectory = My.Computer.FileSystem.CurrentDirectory
        ERwinSpyOpenFileDialog.FileName = ""

        ' Display the Open dialog box.
        If ERwinSpyOpenFileDialog.ShowDialog() = System.Windows.Forms.DialogResult.OK Then
            ' Load file
            Try
                oModel = oApplication.PersistenceUnits.Add("erwin://" & ERwinSpyOpenFileDialog.FileName, "RDO=Yes")
                oBag.Add("Hidden_Model", False)
                oModel.PropertyBag = oBag

                ' Reset the model menu
                PopulateModels()

                ' Locate the new file in the menu. Assumption is that it will be added at the end of PU collection
                For nIdx = mnModelsArray.DropDownItems.Count - 1 To 0 Step -1
                    ' Check if the item has the same name and Id
                    If oApplication.PersistenceUnits(nIdx).Name = oModel.Name And oApplication.PersistenceUnits(nIdx).ObjectId.Equals(oModel.ObjectId) Then
                        Exit For
                    End If
                Next
            Catch ex As Exception
                MsgBox("Failed to open a file with error msg: " & ex.Message)
                Exit Sub
            End Try

            If nIdx = -1 Then
                ' Error
                MsgBox("Internal error while loading " & oModel.Name & " model")
                oModel = Nothing
                ClearViews()
            End If

            ' Activate it
            mnModelsArray_Click(mnModelsArray.DropDownItems.Item(nIdx), New System.EventArgs())
        End If
    End Sub

    ' A model selected
    Private Sub mnModelsArray_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs)
        Dim item As ToolStripMenuItem = DirectCast(eventSender, ToolStripMenuItem)
        Dim Index As Short = item.Tag
        Dim eLevel As SCAPI.SC_SessionLevel
        If (Not item.Checked) Then
            ' Close the current window and open a session with a new one
            If Not oSession Is Nothing Then
                oSession.Close()
            End If
            oSession = Nothing
            oApplication.Sessions.Clear()

            ' Reset menu check
            ClearMenuChecks()

            ' Reset UI
            ClearViews()

            ' Release any active PU
            oPersistenceUnit = Nothing

            ' Open a new session
            Try
                oSession = oApplication.Sessions.Add

                ' Attache to persistence unit.
                eLevel = SCAPI.SC_SessionLevel.SCD_SL_M0 ' Native data

                oPersistenceUnit = oApplication.PersistenceUnits(Index)
                oSession.Open(oPersistenceUnit, eLevel)
            Catch ex As Exception
                MsgBox("Failed to open a session. An error is " & ex.Message)
                oSession = Nothing
                oApplication.Sessions.Clear()
                oPersistenceUnit = Nothing
                Exit Sub
            End Try

            ' Have the open session checked
            item.Checked = True

            ' Enable close open model item
            mnClose.Enabled = True

            ' Prepare a tree
            PrepareObjectTree()

        End If
    End Sub

    ' A metamodel selected
    Private Sub mnMetaModelsArray_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs)
        Dim item As ToolStripMenuItem = DirectCast(eventSender, ToolStripMenuItem)
        Dim Index As Short = item.Tag
        Dim eLevel As SCAPI.SC_SessionLevel
        If (Not item.Checked) Then
            ' Close the current window and open a session with a new one
            If Not oSession Is Nothing Then
                oSession.Close()
            End If
            oSession = Nothing
            oApplication.Sessions.Clear()

            ' Uncheck menus
            ClearMenuChecks()

            ' Reset UI
            ClearViews()

            ' Release any active PU
            oPersistenceUnit = Nothing

            ' Open a new session
            Try
                oSession = oApplication.Sessions.Add

                ' Attache to persistence unit.
                eLevel = SCAPI.SC_SessionLevel.SCD_SL_M1 ' Metadata

                oPersistenceUnit = oApplication.PersistenceUnits(Index)
                oSession.Open(oPersistenceUnit, eLevel)
            Catch ex As Exception
                MsgBox("Failed to open a session. An error is " & ex.Message)
                oSession = Nothing
                oApplication.Sessions.Clear()
                oPersistenceUnit = Nothing
                Exit Sub
            End Try

            ' Have the open session checked
            item.Checked = True

            ' Enable close open model item
            mnClose.Enabled = True

            ' Prepare a tree
            PrepareObjectTree()
        End If
    End Sub

    ' An intrinsic metamodel selected
    Private Sub mnIntrinsicMetamodel_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnIntrinsicMetamodel.Click
        Dim eLevel As SCAPI.SC_SessionLevel
        If (Not mnIntrinsicMetamodel.Checked) Then
            ' Close the current window and open a session with a new one
            If Not oSession Is Nothing Then
                oSession.Close()
            End If
            oSession = Nothing
            oApplication.Sessions.Clear()

            ' Reset menu check
            ClearMenuChecks()

            ' Reset UI
            ClearViews()

            ' Release any active PU
            oPersistenceUnit = Nothing

            ' Open a new session
            Try
                oSession = oApplication.Sessions.Add

                ' Attache to persistence unit.
                eLevel = SCAPI.SC_SessionLevel.SCD_SL_M1 ' Metadata

                oSession.Open(oPersistenceUnit, eLevel)
            Catch ex As Exception
                MsgBox("Failed to open a session. An error is " & ex.Message)
                oSession = Nothing
                oApplication.Sessions.Clear()
                oPersistenceUnit = Nothing
                Exit Sub
            End Try

            ' Have the open session checked
            mnIntrinsicMetamodel.Checked = True

            ' Enable close open model item
            mnClose.Enabled = True

            ' Prepare a tree
            PrepareObjectTree()
        End If
    End Sub

    ' A EM2 model selected
    Private Sub mnEM2ModelsArray_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs)
        Dim item As ToolStripMenuItem = DirectCast(eventSender, ToolStripMenuItem)
        Dim Index As Short = item.Tag
        Dim eLevel As SCAPI.SC_SessionLevel
        Dim oEMXModelSet As SCAPI.ModelSet
        Dim oEM2ModelSet As SCAPI.ModelSet
        If (Not item.Checked) Then
            ' Close the current window and open a session with a new one
            If Not oSession Is Nothing Then
                oSession.Close()
            End If
            oSession = Nothing
            oApplication.Sessions.Clear()

            ' Reset menu check
            ClearMenuChecks()

            ' Reset UI
            ClearViews()

            ' Release any active PU
            oPersistenceUnit = Nothing

            ' Open a new session
            Try
                oSession = oApplication.Sessions.Add

                ' Attache to persistence unit.
                eLevel = SCAPI.SC_SessionLevel.SCD_SL_M0 ' Native data

                oPersistenceUnit = oApplication.PersistenceUnits(Index)

                ' Access the top modelset
                oEMXModelSet = oPersistenceUnit.ModelSet

                ' Access the EM2 owned model set
                oEM2ModelSet = oEMXModelSet.OwnedModelSets("EM2")

                oSession.Open(oEM2ModelSet, eLevel)
            Catch ex As Exception
                MsgBox("Failed to open a session. An error is " & ex.Message)
                oSession = Nothing
                oApplication.Sessions.Clear()
                oPersistenceUnit = Nothing
                Exit Sub
            End Try

            ' Have the open session checked
            item.Checked = True

            ' Enable close open model item
            mnClose.Enabled = True

            ' Prepare a tree
            PrepareObjectTree()
        End If
    End Sub

    ' A EM2 metamodel selected
    Private Sub mnEM2MetaModelsArray_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs)
        Dim item As ToolStripMenuItem = DirectCast(eventSender, ToolStripMenuItem)
        Dim Index As Short = item.Tag
        Dim eLevel As SCAPI.SC_SessionLevel
        Dim oEMXModelSet As SCAPI.ModelSet
        Dim oEM2ModelSet As SCAPI.ModelSet
        If (Not item.Checked) Then
            ' Close the current window and open a session with a new one
            If Not oSession Is Nothing Then
                oSession.Close()
            End If
            oSession = Nothing
            oApplication.Sessions.Clear()

            ' Reset menu check
            ClearMenuChecks()

            ' Reset UI
            ClearViews()

            ' Release any active PU
            oPersistenceUnit = Nothing

            ' Open a new session
            Try
                oSession = oApplication.Sessions.Add

                ' Attache to persistence unit.
                eLevel = SCAPI.SC_SessionLevel.SCD_SL_M1 ' Metadata

                oPersistenceUnit = oApplication.PersistenceUnits(Index)

                ' Access the top modelset
                oEMXModelSet = oPersistenceUnit.ModelSet

                ' Access the EM2 owned model set
                oEM2ModelSet = oEMXModelSet.OwnedModelSets("EM2")

                oSession.Open(oEM2ModelSet, eLevel)
            Catch ex As Exception
                MsgBox("Failed to open a session. An error is " & ex.Message)
                oSession = Nothing
                oApplication.Sessions.Clear()
                oPersistenceUnit = Nothing
                Exit Sub
            End Try

            ' Have the open session checked
            item.Checked = True

            ' Enable close open model item
            mnClose.Enabled = True

            ' Prepare a tree
            PrepareObjectTree()
        End If
    End Sub

    ' A EM2 intrinsic metamodel selected
    Private Sub mnEM2IntrinsicMetamodel_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnEM2IntrinsicMetamodel.Click
        Dim eLevel As SCAPI.SC_SessionLevel
        Dim oBag As SCAPI.PropertyBag
        If (Not mnEM2IntrinsicMetamodel.Checked) Then
            ' Close the current window and open a session with a new one
            If Not oSession Is Nothing Then
                oSession.Close()
            End If
            oSession = Nothing
            oApplication.Sessions.Clear()

            ' Reset menu check
            ClearMenuChecks()

            ' Reset UI
            ClearViews()

            ' Release any active PU
            oPersistenceUnit = Nothing

            ' Open a new session
            Try
                oSession = oApplication.Sessions.Add

                ' Attache to persistence unit.
                eLevel = SCAPI.SC_SessionLevel.SCD_SL_M1 ' Metadata

                ' Access an intrinsic EM2 metamodel. Collect an id from ApplicationEnvironment
                oBag = oApplication.ApplicationEnvironment.PropertyBag("Application", "EM2_Metadata_Class")

                oSession.Open(oBag, eLevel)
            Catch ex As Exception
                MsgBox("Failed to open a session. An error is " & ex.Message)
                oSession = Nothing
                oApplication.Sessions.Clear()
                oPersistenceUnit = Nothing
                Exit Sub
            End Try

            ' Have the open session checked
            mnEM2IntrinsicMetamodel.Checked = True

            ' Enable close open model item
            mnClose.Enabled = True

            ' Prepare a tree
            PrepareObjectTree()
        End If
    End Sub

    ' Close the open model
    Private Sub mnClose_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnClose.Click
        If Not oPersistenceUnit Is Nothing Then
            ' Release the session
            oSession.Close()
            oSession = Nothing
            oApplication.Sessions.Clear()
            ' Remove the unit from available
            oApplication.PersistenceUnits.Remove(oPersistenceUnit, False)
            oPersistenceUnit = Nothing
        End If

        PopulateModels()

        ClearViews()
    End Sub

    Private Sub ClearViews()
        ' Reset the properties listbox
        ClearPropertyView()

        ' reset the model tree
        ClearObjectView()

        ' Disable close open model item
        mnClose.Enabled = False

    End Sub

    Private Sub ClearObjectView()
        ' reset the model tree
        tvObjects.Nodes.Clear()
    End Sub

    Private Sub ClearPropertyView()
        ' Reset the properties listbox
        tvProperties.ColumnHeadersVisible = False
        tvProperties.ScrollBars = ScrollBars.None
        tvProperties.Rows.Clear()
    End Sub

    ' Forms a repository root object
    ' Assumes a session is open
    Private Sub PrepareObjectTree()
        Dim oNode As TreeNode
        Try
            ' Reset the control
            ClearObjectView()

            ' Get the root object
            Dim oRoot As SCAPI.ModelObject

            oRoot = oSession.ModelObjects.Root
            Dim ItemKey As String
            Dim strFlags As String
            If Not (oRoot Is Nothing) Then
                ' Form a root

                ' Build a key. Prefix an object id with a number to make it unique
                ' in case if the view will have another instance of the same object
                ItemKey = "R 0 " & oRoot.ObjectId
                strFlags = ObjectFlags(oRoot)

                oNode = tvObjects.Nodes.Add(ItemKey, "( " & oRoot.ClassName & " ) " & oRoot.Name & IIf(Len(strFlags) > 0, " { " & strFlags & " }", ""))
                oNode.ToolTipText = oRoot.ObjectId
                tvObjects.SelectedNode = oNode
            End If
        Catch ex As Exception
            MsgBox("Failed to init the tree view due to " & ex.Message)
        End Try
    End Sub

    Private Sub mnShowIds_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnShowIds.Click
        ' Check/Uncheck
        mnShowIds.Checked = Not mnShowIds.Checked
        tvObjects.ShowNodeToolTips = mnShowIds.Checked
    End Sub

    Private Sub mnSpyHelp_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnSpyHelp.Click
        ' Check/Uncheck
        mnSpyHelp.Checked = Not mnSpyHelp.Checked

        If Not mnSpyHelp.Checked Then
            ' Help is hiden, adjust views
            SplitContainerModelHelp.Panel2Collapsed = True
        Else
            SplitContainerModelHelp.Panel2Collapsed = False
        End If
    End Sub

    Private Sub mnSyncProp_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnSyncProp.Click
        ' Check/Uncheck
        mnSyncProp.Checked = Not mnSyncProp.Checked
    End Sub

    Private Sub mnIntelPropView_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles mnIntelPropView.Click
        ' Check/Uncheck
        mnIntelPropView.Checked = Not mnIntelPropView.Checked
    End Sub

    ' Populate a tree view with children of the selected object
    Private Sub tvObjects_NodeDblClick(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles tvObjects.NodeMouseDoubleClick
        ' Check if the selected has children
        Dim oLastNode As TreeNode
        Dim oObject As SCAPI.ModelObject
        Dim ItemKey As String
        Dim strFlags As String
        Dim oSelectedCollection As SCAPI.ModelObjects

        Dim oNodeDblClicked As TreeNode = DirectCast(eventArgs, TreeNodeMouseClickEventArgs).Node
        If String.Compare(oNodeDblClicked.Name, tvObjects.SelectedNode.Name) <> 0 Then
            Exit Sub
        End If
        Try
            If oNodeDblClicked.Nodes.Count = 0 Then
                ' Try to expand
                Me.Cursor = System.Windows.Forms.Cursors.WaitCursor
                oLastNode = oNodeDblClicked
                ' Create a subcollection with the selected object as a root
                oSelectedCollection = oSession.ModelObjects.Collect(KeyToObjId(oNodeDblClicked.Name), , 1)
                If (Not (oSelectedCollection Is Nothing)) Then
                    ' Iterate through
                    For Each oObject In oSelectedCollection
                        ' Test if an object is valid
                        Try
                            If oObject.IsValid Then
                                ' Collect flags
                                strFlags = ObjectFlags(oObject)

                                ' Build a key. Prefix an object id with a number to make it unique
                                ' in case if the view will have another instance of the same object
                                ItemKey = "C" & Str(oLastNode.Index + 1) & " " & oObject.ObjectId
                                oLastNode = oNodeDblClicked.Nodes.Add(ItemKey, "( " & oObject.ClassName & " ) " & oObject.Name & IIf(Len(strFlags) > 0, " { " & strFlags & " }", ""))
                                oLastNode.ToolTipText = oObject.ObjectId
                            Else
                                If mnExtraDebug.Checked Then
                                    MsgBox("An object with id: " & oObject.ObjectId & " is not available'")
                                End If
                            End If
                        Catch ex As Exception
                            Continue For
                        End Try
                    Next oObject

                    oNodeDblClicked.Expand()
                End If

                Me.Cursor = System.Windows.Forms.Cursors.Default
            End If

            If mnIntelPropView.Checked Then
                ' Show properties
                ShowProperties()
            End If
        Catch ex As Exception
            Me.Cursor = System.Windows.Forms.Cursors.Default
            MsgBox("Failed to retrieve an object with error " & ex.Message)
        End Try
    End Sub
    ' Retrieves object flags. Aranges them as a string to display
    Private Function ObjectFlags(ByRef oObject As SCAPI.ModelObject) As String
        Dim Flags As SCAPI.SC_ModelObjectFlags
        ObjectFlags = ""
        Try
            ' Retrieve flags
            Flags = oObject.Flags
            ' Parse the result
            If Flags = SCAPI.SC_ModelObjectFlags.SCD_MOF_DONT_CARE Then
                ObjectFlags = ""
            Else
                ' 0 -  Object is a persistence unit if set
                If Flags And SCAPI.SC_ModelObjectFlags.SCD_MOF_PERSISTENCE_UNIT Then ObjectFlags = ObjectFlags & "Persistence Unit;"
                ' 1 -  Object is user-defined if set
                If Flags And SCAPI.SC_ModelObjectFlags.SCD_MOF_USER_DEFINED Then ObjectFlags = ObjectFlags & "User-Defined;"
                ' 2 -  Object is root if set
                If Flags And SCAPI.SC_ModelObjectFlags.SCD_MOF_ROOT = 0 Then ObjectFlags = ObjectFlags & "Root;"
                ' 3 -  Object is maintained by the tool if set
                If Flags And SCAPI.SC_ModelObjectFlags.SCD_MOF_TOOL Then ObjectFlags = ObjectFlags & "Tool;"
                ' 4 -  Object is the default if set
                If Flags And SCAPI.SC_ModelObjectFlags.SCD_MOF_DEFAULT Then ObjectFlags = ObjectFlags & "Default;"
                ' 5 -  Object has been changed in the transaction and has not saved
                If Flags And SCAPI.SC_ModelObjectFlags.SCD_MOF_TRANSACTION Then ObjectFlags = ObjectFlags & "Modified;"
            End If
        Catch ex As Exception
            Dim strName As String
            Try
                strName = oObject.ClassName
            Catch ex2 As Exception
                strName = "<unknown>"
            End Try
            MsgBox("Failed to collect flags for an object of " & strName & " class with error " & ex.Message)
            ObjectFlags = "<Error>"
        End Try
    End Function

    Private Sub tvObjects_NodeClick(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles tvObjects.NodeMouseClick
        Dim oNode As TreeNode = DirectCast(eventArgs, TreeNodeMouseClickEventArgs).Node

        If String.Compare(oNode.Name, tvObjects.SelectedNode.Name) <> 0 Then
            Exit Sub
        End If

        ' Locate the help
        Dim oObject As SCAPI.ModelObject
        If Not oNode Is Nothing Then
            Try
                oObject = oSession.ModelObjects.Item(KeyToObjId((oNode.Name)))
                ' Do we need to referesh properties view?
                If mnSyncProp.Checked Then
                    ShowProperties()
                End If
                ActiveHelp(oObject.ClassName)
            Catch ex As Exception

            End Try
        End If
    End Sub

    Private Function KeyToObjId(ByRef Key As String) As String
        ' Key include prefix to make it unique in case if an object exists more then
        ' once in the object view
        Dim nIdx As Integer
        nIdx = InStr(Key, "{")
        KeyToObjId = Mid(Key, nIdx)
    End Function

    Private Sub ShowProperties()
        ' Check if we have a selection
        Dim oSelNode As TreeNode
        Dim oParentNode As TreeNode
        Dim oObject As SCAPI.ModelObject
        Dim oRootObject As SCAPI.ModelObject
        Dim oProperty As SCAPI.ModelProperty

        If Not tvObjects.SelectedNode Is Nothing Then
            ' Clear the property view
            ClearPropertyView()

            ' Show the header
            tvProperties.ColumnHeadersVisible = True

            ' Show the ScrollBars
            tvProperties.ScrollBars = ScrollBars.Both

            ' Locate a node in Objects view
            oSelNode = tvObjects.SelectedNode
            oParentNode = oSelNode.Parent

            Try
                ' Select an object

                ' We use collect, so object would be selected from a 'right' collection
                ' In some cases, metamodel would the one, this is important for a metaproperty
                ' objects

                ' Locate a root object for a collection
                If oParentNode Is Nothing Then
                    oRootObject = oSession.ModelObjects.Root
                Else
                    oRootObject = oSession.ModelObjects.Item(KeyToObjId((oParentNode.Name)))
                End If

                ' Get an object
                oObject = oSession.ModelObjects.Collect(oRootObject).Item(KeyToObjId((oSelNode.Name)))
                If Not oObject Is Nothing Then
                    ' Look in the object
                    For Each oProperty In oObject.Properties
                        Try
                            ' Add a new row to the grid
                            PopulateGridRow(oProperty)
                        Catch ex As Exception
                            If Err.Number = 35602 Then
                                If mnExtraDebug.Checked Then
                                    MsgBox("Object has more the one instance of property with name " & oProperty.ClassName)
                                End If
                            End If
                            Err.Clear()
                        End Try
                    Next oProperty
                End If
            Catch ex As Exception
                ClearPropertyView()
                MsgBox("Failed to retrieve an object properties with error " & ex.Message)
            End Try
        Else
            ClearPropertyView()
        End If
    End Sub

    ' Prepares the properties grid row
    Private Sub PopulateGridRow(ByRef oProperty As SCAPI.ModelProperty)
        Dim nRow As Integer

        nRow = tvProperties.Rows.Add()
        tvProperties.Rows(nRow).Cells(PropGridRowName).Value = oProperty.ClassName
        ' Populate flags
        PropertyFlags(oProperty, nRow)
        ' Populate values
        PopulateValues(oProperty, nRow)
    End Sub

    ' Retrieves property flags and value type.
    Private Sub PropertyFlags(ByRef oProperty As SCAPI.ModelProperty, ByRef nRow As Integer)
        Dim Flags As SCAPI.SC_ModelPropertyFlags
        Dim strDatatype(25) As String
        ' Populate it
        strDatatype(0) = "Null" : strDatatype(1) = "I2" : strDatatype(2) = "I4"
        strDatatype(3) = "UI1" : strDatatype(4) = "R4" : strDatatype(5) = "R8"
        strDatatype(6) = "Bool" : strDatatype(7) = "$$" : strDatatype(8) = "IU"
        strDatatype(9) = "ID" : strDatatype(10) = "Date" : strDatatype(11) = "Str"
        strDatatype(12) = "UI2" : strDatatype(13) = "UI4" : strDatatype(14) = "Guid"
        strDatatype(15) = "Id" : strDatatype(16) = "Blob" : strDatatype(17) = "Def"
        strDatatype(18) = "I1" : strDatatype(19) = "IT" : strDatatype(20) = "UIT"
        strDatatype(21) = "Rect" : strDatatype(22) = "Pnt" : strDatatype(23) = "I8"
        strDatatype(24) = "UI8" : strDatatype(25) = "Size"

        Try
            ' Retrieve flags
            Flags = oProperty.Flags

            ' Get the value type
            Dim eType As SCAPI.SC_ValueTypes

            eType = oProperty.DataType(0)

            ' Datatype
            tvProperties.Rows(nRow).Cells(PropGridRowDataType).Value = strDatatype(CInt(eType))

            ' Parse the result
            ' 0 -  Property has a NULL value if set
            If Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_NULL Then tvProperties.Rows(nRow).Cells(PropGridRowNull).Style.BackColor = System.Drawing.Color.Red
            ' 1 -  Property is user-defined if set
            If Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_USER_DEFINED Then tvProperties.Rows(nRow).Cells(PropGridRowUserDef).Style.BackColor = System.Drawing.Color.Red
            ' 2 -  Property is scalar if set
            If (Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_SCALAR) = 0 Then tvProperties.Rows(nRow).Cells(PropGridRowVector).Style.BackColor = System.Drawing.Color.Red
            ' 3 -  Property is maintained by the tool, if set
            If Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_TOOL Then tvProperties.Rows(nRow).Cells(PropGridRowTool).Style.BackColor = System.Drawing.Color.Red
            ' 4 -  Property is read-only if set
            If Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_READ_ONLY Then tvProperties.Rows(nRow).Cells(PropGridRowReadOnly).Style.BackColor = System.Drawing.Color.Red
            ' 5 -  Property has inherited/calculated/derived value if set
            If Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_DERIVED Then tvProperties.Rows(nRow).Cells(PropGridRowDerived).Style.BackColor = System.Drawing.Color.Red

            ' Facets
            Dim aFacetNamesTrue() As String = Nothing
            Dim aFacetNamesFalse() As String = Nothing
            Dim bFacetFound As Boolean

            bFacetFound = oProperty.GetValueFacetNames(CType(aFacetNamesTrue, Array), CType(aFacetNamesFalse, Array))
            Dim sFacets As String
            Dim i As Short
            If bFacetFound = True Then
                ' Populate the facet cell
                sFacets = ""
                If IsArrayEmpty(aFacetNamesTrue) = False Then
                    For i = LBound(aFacetNamesTrue, 1) To UBound(aFacetNamesTrue, 1)
                        If Len(sFacets) = 0 Then
                            ' New string
                            sFacets = aFacetNamesTrue(i)
                        Else
                            ' Add another name
                            sFacets = sFacets & ", " & aFacetNamesTrue(i)
                        End If
                    Next
                    ' Place in the grid
                    tvProperties.Rows(nRow).Cells(PropGridRowFacetsTrue).Value = sFacets
                End If

                sFacets = ""
                If IsArrayEmpty(aFacetNamesFalse) = False Then
                    For i = LBound(aFacetNamesFalse, 1) To UBound(aFacetNamesFalse, 1)
                        If Len(sFacets) = 0 Then
                            ' New string
                            sFacets = aFacetNamesFalse(i)
                        Else
                            ' Add another name
                            sFacets = sFacets & ", " & aFacetNamesFalse(i)
                        End If
                    Next
                    ' Place in the grid
                    tvProperties.Rows(nRow).Cells(PropGridRowFacetsFalse).Value = sFacets
                End If
            End If
        Catch ex As Exception
            Dim strName As String
            Try
                strName = oProperty.ClassName
            Catch ex2 As Exception
                strName = "<unknown>"
            End Try
            MsgBox("Failed to collect flags for a property of " & strName & " class with error " & ex.Message)
        End Try
    End Sub

    ' Populate a grid for a specific property in Properties ListView
    Private Sub PopulateValues(ByRef oProperty As SCAPI.ModelProperty, ByRef nRow As Integer)
        Dim nCount As Integer
        Dim sNativeValue As String = ""
        Dim sTmpNativeValue As String
        Dim sStringValue As String = ""
        Dim sTmpStringValue As String
        Dim nIdx As Integer
        Try
            ' What type of a property we have
            If oProperty.Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_SCALAR Then
                ' SCalar value
                tvProperties.Rows(nRow).Cells(PropGridRowValue).Value = RetrieveValue(oProperty)
                tvProperties.Rows(nRow).Cells(PropGridRowAsString).Value = oProperty.FormatAsString
            Else
                ' Vector value
                nCount = oProperty.Count

                If nCount > 0 Then
                    For nIdx = 0 To nCount - 1
                        ' Get the native value
                        sTmpNativeValue = RetrieveValue(oProperty, nIdx)
                        ' Get As String value
                        sTmpStringValue = oProperty.Value(nIdx, SCAPI.SC_ValueTypes.SCVT_BSTR)

                        ' Is this a first line
                        If nIdx = 0 Then
                            sNativeValue = sTmpNativeValue
                            sStringValue = sTmpStringValue
                        Else
                            sNativeValue = sNativeValue & Chr(13) & sTmpNativeValue
                            sStringValue = sStringValue & Chr(13) & sTmpStringValue
                        End If
                    Next nIdx

                    ' Populate the grid
                    tvProperties.Rows(nRow).Cells(PropGridRowValue).Value = sNativeValue
                    tvProperties.Rows(nRow).Cells(PropGridRowAsString).Value = sStringValue
                End If
            End If
        Catch ex As Exception
            Dim strName As String
            Try
                strName = oProperty.ClassName
            Catch ex2 As Exception
                strName = "<unknown>"
            End Try
            MsgBox("Failed to populate property " & strName & " with error " & ex.Message)
        End Try
    End Sub

    ' Retrieve a property or a property element value
    Private Function RetrieveValue(ByRef oProperty As SCAPI.ModelProperty, Optional ByRef nIndex As Integer = -1) As String
        Try
            ' What type of a property we have
            Dim bScalar As Boolean
            bScalar = (oProperty.Flags And SCAPI.SC_ModelPropertyFlags.SCD_MPF_SCALAR)

            ' Retrieve value in native format
            ' We use SCAPI value types since they provide more precise info
            Dim eType As SCAPI.SC_ValueTypes
            Dim ArrayLong() As Integer

            If bScalar Then
                ' SCalar value
                eType = oProperty.DataType
            Else
                ' Vector value
                eType = oProperty.DataType(nIndex)
            End If

            Dim strNative As String

            ' Parse the value.
            Select Case eType
                Case SCAPI.SC_ValueTypes.SCVT_I2, SCAPI.SC_ValueTypes.SCVT_I4, SCAPI.SC_ValueTypes.SCVT_UI1, SCAPI.SC_ValueTypes.SCVT_UI2, SCAPI.SC_ValueTypes.SCVT_UI4, SCAPI.SC_ValueTypes.SCVT_I1, SCAPI.SC_ValueTypes.SCVT_INT, SCAPI.SC_ValueTypes.SCVT_UINT, SCAPI.SC_ValueTypes.SCVT_I8, SCAPI.SC_ValueTypes.SCVT_UI8
                    ' This is all numeric
                    If bScalar Then
                        strNative = CStr(oProperty.Value)
                    Else
                        strNative = CStr(oProperty.Value(nIndex))
                    End If
                Case SCAPI.SC_ValueTypes.SCVT_R4, SCAPI.SC_ValueTypes.SCVT_R8
                    ' This is all float
                    If bScalar Then
                        strNative = CStr(oProperty.Value)
                    Else
                        strNative = CStr(oProperty.Value(nIndex))
                    End If
                Case SCAPI.SC_ValueTypes.SCVT_BOOLEAN
                    ' This is boolean
                    If bScalar Then
                        strNative = CStr(oProperty.Value)
                    Else
                        strNative = CStr(oProperty.Value(nIndex))
                    End If
                Case SCAPI.SC_ValueTypes.SCVT_CURRENCY
                    ' This is currency
                    If bScalar Then
                        strNative = CStr(oProperty.Value)
                    Else
                        strNative = CStr(oProperty.Value(nIndex))
                    End If
                Case SCAPI.SC_ValueTypes.SCVT_DATE
                    ' This is date
                    If bScalar Then
                        strNative = Format(oProperty.Value, "General Date")
                    Else
                        strNative = Format(oProperty.Value(nIndex), "General Date")
                    End If
                Case SCAPI.SC_ValueTypes.SCVT_BSTR ', SCAPI.SC_ValueTypes.SCVT_BRANCH_LOG
                    ' This is string
                    If bScalar Then
                        strNative = oProperty.Value
                    Else
                        strNative = oProperty.Value(nIndex)
                    End If
                Case SCAPI.SC_ValueTypes.SCVT_GUID, SCAPI.SC_ValueTypes.SCVT_OBJID
                    ' This is guid, objectid, class id
                    If bScalar Then
                        strNative = oProperty.Value
                    Else
                        strNative = oProperty.Value(nIndex)
                    End If
                Case SCAPI.SC_ValueTypes.SCVT_BLOB
                    ' This is unformated data
                    strNative = "<blob>"
                Case SCAPI.SC_ValueTypes.SCVT_RECT
                    ' This is a rectange
                    If bScalar Then
                        ArrayLong = oProperty.Value
                    Else
                        ArrayLong = oProperty.Value(nIndex)
                    End If

                    strNative = "(" & CStr(ArrayLong(0)) & "," & CStr(ArrayLong(1)) & "," & CStr(ArrayLong(2)) & "," & CStr(ArrayLong(3)) & ")"
                Case SCAPI.SC_ValueTypes.SCVT_POINT
                    ' This is a rectange
                    If bScalar Then
                        ArrayLong = oProperty.Value
                    Else
                        ArrayLong = oProperty.Value(nIndex)
                    End If

                    strNative = "(" & CStr(ArrayLong(0)) & "," & CStr(ArrayLong(1)) & ")"
                Case SCAPI.SC_ValueTypes.SCVT_SIZE
                    ' This is a size
                    If bScalar Then
                        ArrayLong = oProperty.Value
                    Else
                        ArrayLong = oProperty.Value(nIndex)
                    End If

                    strNative = CStr(ArrayLong(0)) & "x" & CStr(ArrayLong(1))
                Case Else
                    ' error, format is not supported
                    strNative = "<error: variant type - " & TypeName(oProperty.Value) & " SCAPI type - " & Str(eType)
            End Select

            RetrieveValue = strNative
        Catch ex As Exception
            Dim strName As String
            Try
                strName = oProperty.ClassName
            Catch ex2 As Exception
                strName = "<unknown>"
            End Try
            RetrieveValue = "Failed to populate property " & strName & " with error " & ex.Message
        End Try
    End Function

    Private Sub ActiveHelp(ByRef Locator As String)
        Dim strLocator As String = ""

        If Len(Locator) > 0 Then
            ' Replace all spaces with underscore
            strLocator = Replace(Locator, " ", "_")
            ' Add bookmark
            strLocator = "#_" & strLocator
        End If
        wbHelp.Navigate(New System.Uri("file://" & My.Application.Info.DirectoryPath & "\erwin Spy Help.htm" & strLocator))
    End Sub

    Private Function IsArrayEmpty(ByRef varArray As Object) As Boolean
        Dim Upper As Integer

        On Error Resume Next
        IsArrayEmpty = True

        Upper = UBound(varArray)
        If Err.Number Then
            If Err.Number = 9 Then
                IsArrayEmpty = True
            Else
                With Err()
                    MsgBox("Error:" & Err.Number & "-" & Err.Description)
                End With
                Exit Function
            End If
        Else
            IsArrayEmpty = False
        End If
        On Error GoTo 0
    End Function
End Class