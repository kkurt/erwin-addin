<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class frmERwinSpy
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()> _
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Dim resources As System.ComponentModel.ComponentResourceManager = New System.ComponentModel.ComponentResourceManager(GetType(frmERwinSpy))
        Me.tvObjectsToolTip = New System.Windows.Forms.ToolTip(Me.components)
        Me.frmSplitH = New System.Windows.Forms.GroupBox()
        Me.tvObjects = New System.Windows.Forms.TreeView()
        Me.ERwinSpyMainMenu = New System.Windows.Forms.MenuStrip()
        Me.mnFile = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnFileOpen = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnClose = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnSep2 = New System.Windows.Forms.ToolStripSeparator()
        Me.mnOptions = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnShowIds = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnSyncProp = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnIntelPropView = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnExtraDebug = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnSep1 = New System.Windows.Forms.ToolStripSeparator()
        Me.mnExit = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnModels = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnModelsArray = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnMetaModelsArray = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnIntrinsicMetamodel = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnEM2ModelsArray = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnEM2MetaModelsArray = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnEM2IntrinsicMetamodel = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnHelp = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnSpyHelp = New System.Windows.Forms.ToolStripMenuItem()
        Me.mnAbout = New System.Windows.Forms.ToolStripMenuItem()
        Me.wbHelp = New System.Windows.Forms.WebBrowser()
        Me.ERwinSpyOpenFileDialog = New System.Windows.Forms.OpenFileDialog()
        Me.LabelHelp = New System.Windows.Forms.Label()
        Me.ERwinSpyLabel = New System.Windows.Forms.Label()
        Me.LabelProperties = New System.Windows.Forms.Label()
        Me.LabelObjects = New System.Windows.Forms.Label()
        Me.SplitContainerObjectProperty = New System.Windows.Forms.SplitContainer()
        Me.tvProperties = New System.Windows.Forms.DataGridView()
        Me.PropertyName = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyDataType = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyValue = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyValueAsString = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyNULL = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyUserDefined = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyVector = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyTool = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyReadOnly = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyDerived = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyFacetsTrue = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.PropertyFacetsFalse = New System.Windows.Forms.DataGridViewTextBoxColumn()
        Me.SplitContainerModelHelp = New System.Windows.Forms.SplitContainer()
        Me.TableLayoutPanelMain = New System.Windows.Forms.TableLayoutPanel()
        Me.ERwinSpyMainMenu.SuspendLayout()
        CType(Me.SplitContainerObjectProperty, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SplitContainerObjectProperty.Panel1.SuspendLayout()
        Me.SplitContainerObjectProperty.Panel2.SuspendLayout()
        Me.SplitContainerObjectProperty.SuspendLayout()
        CType(Me.tvProperties, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.SplitContainerModelHelp, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SplitContainerModelHelp.Panel1.SuspendLayout()
        Me.SplitContainerModelHelp.Panel2.SuspendLayout()
        Me.SplitContainerModelHelp.SuspendLayout()
        Me.TableLayoutPanelMain.SuspendLayout()
        Me.SuspendLayout()
        '
        'frmSplitH
        '
        Me.frmSplitH.BackColor = System.Drawing.SystemColors.Control
        Me.frmSplitH.Dock = System.Windows.Forms.DockStyle.Top
        Me.frmSplitH.Font = New System.Drawing.Font("Arial", 8.0!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.frmSplitH.ForeColor = System.Drawing.SystemColors.ControlText
        Me.frmSplitH.Location = New System.Drawing.Point(0, 21)
        Me.frmSplitH.Name = "frmSplitH"
        Me.frmSplitH.Padding = New System.Windows.Forms.Padding(0)
        Me.frmSplitH.RightToLeft = System.Windows.Forms.RightToLeft.No
        Me.frmSplitH.Size = New System.Drawing.Size(660, 3)
        Me.frmSplitH.TabIndex = 9
        Me.frmSplitH.TabStop = False
        Me.tvObjectsToolTip.SetToolTip(Me.frmSplitH, "Resize")
        '
        'tvObjects
        '
        Me.tvObjects.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tvObjects.Location = New System.Drawing.Point(0, 17)
        Me.tvObjects.Name = "tvObjects"
        Me.tvObjects.Size = New System.Drawing.Size(324, 259)
        Me.tvObjects.TabIndex = 1
        Me.tvObjectsToolTip.SetToolTip(Me.tvObjects, "Double click to expand")
        '
        'ERwinSpyMainMenu
        '
        Me.ERwinSpyMainMenu.Items.AddRange(New System.Windows.Forms.ToolStripItem() {Me.mnFile, Me.mnModels, Me.mnHelp})
        Me.ERwinSpyMainMenu.Location = New System.Drawing.Point(0, 0)
        Me.ERwinSpyMainMenu.Name = "ERwinSpyMainMenu"
        Me.ERwinSpyMainMenu.Size = New System.Drawing.Size(666, 24)
        Me.ERwinSpyMainMenu.TabIndex = 10
        '
        'mnFile
        '
        Me.mnFile.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.mnFileOpen, Me.mnClose, Me.mnSep2, Me.mnOptions, Me.mnSep1, Me.mnExit})
        Me.mnFile.Name = "mnFile"
        Me.mnFile.Size = New System.Drawing.Size(37, 20)
        Me.mnFile.Text = "&File"
        '
        'mnFileOpen
        '
        Me.mnFileOpen.Name = "mnFileOpen"
        Me.mnFileOpen.ShortcutKeys = CType((System.Windows.Forms.Keys.Control Or System.Windows.Forms.Keys.O), System.Windows.Forms.Keys)
        Me.mnFileOpen.Size = New System.Drawing.Size(179, 22)
        Me.mnFileOpen.Text = "File &Open ..."
        '
        'mnClose
        '
        Me.mnClose.Name = "mnClose"
        Me.mnClose.Size = New System.Drawing.Size(179, 22)
        Me.mnClose.Text = "&Close Open Model"
        '
        'mnSep2
        '
        Me.mnSep2.Name = "mnSep2"
        Me.mnSep2.Size = New System.Drawing.Size(176, 6)
        '
        'mnOptions
        '
        Me.mnOptions.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.mnShowIds, Me.mnSyncProp, Me.mnIntelPropView, Me.mnExtraDebug})
        Me.mnOptions.Name = "mnOptions"
        Me.mnOptions.Size = New System.Drawing.Size(179, 22)
        Me.mnOptions.Text = "&Options"
        '
        'mnShowIds
        '
        Me.mnShowIds.Name = "mnShowIds"
        Me.mnShowIds.Size = New System.Drawing.Size(211, 22)
        Me.mnShowIds.Text = "Show Ids"
        '
        'mnSyncProp
        '
        Me.mnSyncProp.Name = "mnSyncProp"
        Me.mnSyncProp.Size = New System.Drawing.Size(211, 22)
        Me.mnSyncProp.Text = "Sync with Properties View"
        '
        'mnIntelPropView
        '
        Me.mnIntelPropView.Name = "mnIntelPropView"
        Me.mnIntelPropView.Size = New System.Drawing.Size(211, 22)
        Me.mnIntelPropView.Text = "Intelligent Properties View"
        '
        'mnExtraDebug
        '
        Me.mnExtraDebug.Name = "mnExtraDebug"
        Me.mnExtraDebug.Size = New System.Drawing.Size(211, 22)
        Me.mnExtraDebug.Text = "Extra Debug Info"
        '
        'mnSep1
        '
        Me.mnSep1.Name = "mnSep1"
        Me.mnSep1.Size = New System.Drawing.Size(176, 6)
        '
        'mnExit
        '
        Me.mnExit.Name = "mnExit"
        Me.mnExit.Size = New System.Drawing.Size(179, 22)
        Me.mnExit.Text = "E&xit"
        '
        'mnModels
        '
        Me.mnModels.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.mnModelsArray, Me.mnMetaModelsArray, Me.mnEM2ModelsArray, Me.mnEM2MetaModelsArray})
        Me.mnModels.Name = "mnModels"
        Me.mnModels.Size = New System.Drawing.Size(58, 20)
        Me.mnModels.Text = "&Models"
        '
        'mnModelsArray
        '
        Me.mnModelsArray.Name = "mnModelsArray"
        Me.mnModelsArray.Size = New System.Drawing.Size(185, 22)
        Me.mnModelsArray.Text = "&Models"
        '
        'mnMetaModelsArray
        '
        Me.mnMetaModelsArray.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.mnIntrinsicMetamodel})
        Me.mnMetaModelsArray.Name = "mnMetaModelsArray"
        Me.mnMetaModelsArray.Size = New System.Drawing.Size(185, 22)
        Me.mnMetaModelsArray.Text = "M&etaModels"
        '
        'mnIntrinsicMetamodel
        '
        Me.mnIntrinsicMetamodel.Name = "mnIntrinsicMetamodel"
        Me.mnIntrinsicMetamodel.Size = New System.Drawing.Size(180, 22)
        Me.mnIntrinsicMetamodel.Text = "Intrinsic Metamodel"
        '
        'mnEM2ModelsArray
        '
        Me.mnEM2ModelsArray.Name = "mnEM2ModelsArray"
        Me.mnEM2ModelsArray.Size = New System.Drawing.Size(185, 22)
        Me.mnEM2ModelsArray.Text = "&EM2 ModelSets"
        '
        'mnEM2MetaModelsArray
        '
        Me.mnEM2MetaModelsArray.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.mnEM2IntrinsicMetamodel})
        Me.mnEM2MetaModelsArray.Name = "mnEM2MetaModelsArray"
        Me.mnEM2MetaModelsArray.Size = New System.Drawing.Size(185, 22)
        Me.mnEM2MetaModelsArray.Text = "EM&2 ModelSets Meta"
        '
        'mnEM2IntrinsicMetamodel
        '
        Me.mnEM2IntrinsicMetamodel.Name = "mnEM2IntrinsicMetamodel"
        Me.mnEM2IntrinsicMetamodel.Size = New System.Drawing.Size(206, 22)
        Me.mnEM2IntrinsicMetamodel.Text = "EM2 Intrinsic Metamodel"
        '
        'mnHelp
        '
        Me.mnHelp.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.mnSpyHelp, Me.mnAbout})
        Me.mnHelp.Name = "mnHelp"
        Me.mnHelp.Size = New System.Drawing.Size(44, 20)
        Me.mnHelp.Text = "&Help"
        '
        'mnSpyHelp
        '
        Me.mnSpyHelp.Name = "mnSpyHelp"
        Me.mnSpyHelp.Size = New System.Drawing.Size(173, 22)
        Me.mnSpyHelp.Text = "erwin Spy &Help"
        '
        'mnAbout
        '
        Me.mnAbout.Name = "mnAbout"
        Me.mnAbout.Size = New System.Drawing.Size(173, 22)
        Me.mnAbout.Text = "&About erwin Spy ..."
        '
        'wbHelp
        '
        Me.wbHelp.AllowWebBrowserDrop = False
        Me.wbHelp.Dock = System.Windows.Forms.DockStyle.Fill
        Me.wbHelp.Location = New System.Drawing.Point(0, 24)
        Me.wbHelp.Name = "wbHelp"
        Me.wbHelp.Size = New System.Drawing.Size(660, 75)
        Me.wbHelp.TabIndex = 10
        '
        'LabelHelp
        '
        Me.LabelHelp.BackColor = System.Drawing.SystemColors.Control
        Me.LabelHelp.Cursor = System.Windows.Forms.Cursors.Default
        Me.LabelHelp.Dock = System.Windows.Forms.DockStyle.Top
        Me.LabelHelp.Font = New System.Drawing.Font("Arial", 8.0!, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.LabelHelp.ForeColor = System.Drawing.SystemColors.ControlText
        Me.LabelHelp.Location = New System.Drawing.Point(0, 0)
        Me.LabelHelp.Name = "LabelHelp"
        Me.LabelHelp.RightToLeft = System.Windows.Forms.RightToLeft.No
        Me.LabelHelp.Size = New System.Drawing.Size(660, 21)
        Me.LabelHelp.TabIndex = 6
        Me.LabelHelp.Text = "Help"
        '
        'ERwinSpyLabel
        '
        Me.ERwinSpyLabel.BackColor = System.Drawing.SystemColors.Control
        Me.ERwinSpyLabel.Cursor = System.Windows.Forms.Cursors.Default
        Me.ERwinSpyLabel.Dock = System.Windows.Forms.DockStyle.Fill
        Me.ERwinSpyLabel.Font = New System.Drawing.Font("Microsoft Sans Serif", 21.75!, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.ERwinSpyLabel.ForeColor = System.Drawing.Color.Blue
        Me.ERwinSpyLabel.Location = New System.Drawing.Point(3, 0)
        Me.ERwinSpyLabel.Name = "ERwinSpyLabel"
        Me.ERwinSpyLabel.RightToLeft = System.Windows.Forms.RightToLeft.No
        Me.ERwinSpyLabel.Size = New System.Drawing.Size(660, 42)
        Me.ERwinSpyLabel.TabIndex = 4
        Me.ERwinSpyLabel.Text = "erwin Spy"
        Me.ERwinSpyLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter
        '
        'LabelProperties
        '
        Me.LabelProperties.BackColor = System.Drawing.SystemColors.Control
        Me.LabelProperties.Cursor = System.Windows.Forms.Cursors.Default
        Me.LabelProperties.Dock = System.Windows.Forms.DockStyle.Top
        Me.LabelProperties.Font = New System.Drawing.Font("Arial", 9.0!, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.LabelProperties.ForeColor = System.Drawing.SystemColors.ControlText
        Me.LabelProperties.Location = New System.Drawing.Point(0, 0)
        Me.LabelProperties.Name = "LabelProperties"
        Me.LabelProperties.RightToLeft = System.Windows.Forms.RightToLeft.No
        Me.LabelProperties.Size = New System.Drawing.Size(332, 17)
        Me.LabelProperties.TabIndex = 2
        Me.LabelProperties.Text = "Properties"
        Me.LabelProperties.TextAlign = System.Drawing.ContentAlignment.TopRight
        '
        'LabelObjects
        '
        Me.LabelObjects.BackColor = System.Drawing.SystemColors.Control
        Me.LabelObjects.Cursor = System.Windows.Forms.Cursors.Default
        Me.LabelObjects.Dock = System.Windows.Forms.DockStyle.Top
        Me.LabelObjects.Font = New System.Drawing.Font("Arial", 9.0!, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.LabelObjects.ForeColor = System.Drawing.SystemColors.ControlText
        Me.LabelObjects.Location = New System.Drawing.Point(0, 0)
        Me.LabelObjects.Name = "LabelObjects"
        Me.LabelObjects.RightToLeft = System.Windows.Forms.RightToLeft.No
        Me.LabelObjects.Size = New System.Drawing.Size(324, 17)
        Me.LabelObjects.TabIndex = 0
        Me.LabelObjects.Text = "Objects"
        '
        'SplitContainerObjectProperty
        '
        Me.SplitContainerObjectProperty.Dock = System.Windows.Forms.DockStyle.Fill
        Me.SplitContainerObjectProperty.Location = New System.Drawing.Point(0, 0)
        Me.SplitContainerObjectProperty.Name = "SplitContainerObjectProperty"
        '
        'SplitContainerObjectProperty.Panel1
        '
        Me.SplitContainerObjectProperty.Panel1.Controls.Add(Me.tvObjects)
        Me.SplitContainerObjectProperty.Panel1.Controls.Add(Me.LabelObjects)
        '
        'SplitContainerObjectProperty.Panel2
        '
        Me.SplitContainerObjectProperty.Panel2.Controls.Add(Me.tvProperties)
        Me.SplitContainerObjectProperty.Panel2.Controls.Add(Me.LabelProperties)
        Me.SplitContainerObjectProperty.Size = New System.Drawing.Size(660, 276)
        Me.SplitContainerObjectProperty.SplitterDistance = 324
        Me.SplitContainerObjectProperty.TabIndex = 5
        '
        'tvProperties
        '
        Me.tvProperties.AllowUserToAddRows = False
        Me.tvProperties.AllowUserToDeleteRows = False
        Me.tvProperties.AllowUserToOrderColumns = True
        Me.tvProperties.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill
        Me.tvProperties.BackgroundColor = System.Drawing.SystemColors.Window
        Me.tvProperties.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D
        Me.tvProperties.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize
        Me.tvProperties.ColumnHeadersVisible = False
        Me.tvProperties.Columns.AddRange(New System.Windows.Forms.DataGridViewColumn() {Me.PropertyName, Me.PropertyDataType, Me.PropertyValue, Me.PropertyValueAsString, Me.PropertyNULL, Me.PropertyUserDefined, Me.PropertyVector, Me.PropertyTool, Me.PropertyReadOnly, Me.PropertyDerived, Me.PropertyFacetsTrue, Me.PropertyFacetsFalse})
        Me.tvProperties.Dock = System.Windows.Forms.DockStyle.Fill
        Me.tvProperties.Location = New System.Drawing.Point(0, 17)
        Me.tvProperties.MultiSelect = False
        Me.tvProperties.Name = "tvProperties"
        Me.tvProperties.ReadOnly = True
        Me.tvProperties.RowHeadersVisible = False
        Me.tvProperties.ScrollBars = System.Windows.Forms.ScrollBars.None
        Me.tvProperties.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect
        Me.tvProperties.Size = New System.Drawing.Size(332, 259)
        Me.tvProperties.TabIndex = 3
        '
        'PropertyName
        '
        Me.PropertyName.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.AllCells
        Me.PropertyName.Frozen = True
        Me.PropertyName.HeaderText = "Property"
        Me.PropertyName.Name = "PropertyName"
        Me.PropertyName.ReadOnly = True
        Me.PropertyName.Width = 5
        '
        'PropertyDataType
        '
        Me.PropertyDataType.AutoSizeMode = System.Windows.Forms.DataGridViewAutoSizeColumnMode.AllCells
        Me.PropertyDataType.HeaderText = "DT"
        Me.PropertyDataType.Name = "PropertyDataType"
        Me.PropertyDataType.ReadOnly = True
        Me.PropertyDataType.Width = 5
        '
        'PropertyValue
        '
        Me.PropertyValue.HeaderText = "Value"
        Me.PropertyValue.MinimumWidth = 46
        Me.PropertyValue.Name = "PropertyValue"
        Me.PropertyValue.ReadOnly = True
        '
        'PropertyValueAsString
        '
        Me.PropertyValueAsString.HeaderText = "As String"
        Me.PropertyValueAsString.MinimumWidth = 83
        Me.PropertyValueAsString.Name = "PropertyValueAsString"
        Me.PropertyValueAsString.ReadOnly = True
        '
        'PropertyNULL
        '
        Me.PropertyNULL.HeaderText = "NL"
        Me.PropertyNULL.MinimumWidth = 25
        Me.PropertyNULL.Name = "PropertyNULL"
        Me.PropertyNULL.ReadOnly = True
        '
        'PropertyUserDefined
        '
        Me.PropertyUserDefined.HeaderText = "UD"
        Me.PropertyUserDefined.MinimumWidth = 25
        Me.PropertyUserDefined.Name = "PropertyUserDefined"
        Me.PropertyUserDefined.ReadOnly = True
        '
        'PropertyVector
        '
        Me.PropertyVector.HeaderText = "VC"
        Me.PropertyVector.MinimumWidth = 25
        Me.PropertyVector.Name = "PropertyVector"
        Me.PropertyVector.ReadOnly = True
        '
        'PropertyTool
        '
        Me.PropertyTool.HeaderText = "TL"
        Me.PropertyTool.MinimumWidth = 25
        Me.PropertyTool.Name = "PropertyTool"
        Me.PropertyTool.ReadOnly = True
        '
        'PropertyReadOnly
        '
        Me.PropertyReadOnly.HeaderText = "RO"
        Me.PropertyReadOnly.MinimumWidth = 25
        Me.PropertyReadOnly.Name = "PropertyReadOnly"
        Me.PropertyReadOnly.ReadOnly = True
        '
        'PropertyDerived
        '
        Me.PropertyDerived.HeaderText = "DR"
        Me.PropertyDerived.MinimumWidth = 25
        Me.PropertyDerived.Name = "PropertyDerived"
        Me.PropertyDerived.ReadOnly = True
        '
        'PropertyFacetsTrue
        '
        Me.PropertyFacetsTrue.HeaderText = "Facets True"
        Me.PropertyFacetsTrue.MinimumWidth = 96
        Me.PropertyFacetsTrue.Name = "PropertyFacetsTrue"
        Me.PropertyFacetsTrue.ReadOnly = True
        '
        'PropertyFacetsFalse
        '
        Me.PropertyFacetsFalse.HeaderText = "Facets False"
        Me.PropertyFacetsFalse.MinimumWidth = 100
        Me.PropertyFacetsFalse.Name = "PropertyFacetsFalse"
        Me.PropertyFacetsFalse.ReadOnly = True
        '
        'SplitContainerModelHelp
        '
        Me.SplitContainerModelHelp.Dock = System.Windows.Forms.DockStyle.Fill
        Me.SplitContainerModelHelp.Location = New System.Drawing.Point(3, 45)
        Me.SplitContainerModelHelp.Name = "SplitContainerModelHelp"
        Me.SplitContainerModelHelp.Orientation = System.Windows.Forms.Orientation.Horizontal
        '
        'SplitContainerModelHelp.Panel1
        '
        Me.SplitContainerModelHelp.Panel1.Controls.Add(Me.SplitContainerObjectProperty)
        '
        'SplitContainerModelHelp.Panel2
        '
        Me.SplitContainerModelHelp.Panel2.Controls.Add(Me.wbHelp)
        Me.SplitContainerModelHelp.Panel2.Controls.Add(Me.frmSplitH)
        Me.SplitContainerModelHelp.Panel2.Controls.Add(Me.LabelHelp)
        Me.SplitContainerModelHelp.Size = New System.Drawing.Size(660, 379)
        Me.SplitContainerModelHelp.SplitterDistance = 276
        Me.SplitContainerModelHelp.TabIndex = 5
        '
        'TableLayoutPanelMain
        '
        Me.TableLayoutPanelMain.ColumnCount = 1
        Me.TableLayoutPanelMain.ColumnStyles.Add(New System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100.0!))
        Me.TableLayoutPanelMain.Controls.Add(Me.ERwinSpyLabel, 0, 0)
        Me.TableLayoutPanelMain.Controls.Add(Me.SplitContainerModelHelp, 0, 1)
        Me.TableLayoutPanelMain.Dock = System.Windows.Forms.DockStyle.Fill
        Me.TableLayoutPanelMain.Location = New System.Drawing.Point(0, 24)
        Me.TableLayoutPanelMain.Name = "TableLayoutPanelMain"
        Me.TableLayoutPanelMain.RowCount = 2
        Me.TableLayoutPanelMain.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10.0!))
        Me.TableLayoutPanelMain.RowStyles.Add(New System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 90.0!))
        Me.TableLayoutPanelMain.Size = New System.Drawing.Size(666, 427)
        Me.TableLayoutPanelMain.TabIndex = 11
        '
        'frmERwinSpy
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(7.0!, 14.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.BackColor = System.Drawing.SystemColors.Control
        Me.ClientSize = New System.Drawing.Size(666, 451)
        Me.Controls.Add(Me.TableLayoutPanelMain)
        Me.Controls.Add(Me.ERwinSpyMainMenu)
        Me.Cursor = System.Windows.Forms.Cursors.Default
        Me.Font = New System.Drawing.Font("Arial", 8.25!, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, CType(0, Byte))
        Me.ForeColor = System.Drawing.SystemColors.WindowText
        Me.Icon = CType(resources.GetObject("$this.Icon"), System.Drawing.Icon)
        Me.Location = New System.Drawing.Point(73, 118)
        Me.Name = "frmERwinSpy"
        Me.RightToLeft = System.Windows.Forms.RightToLeft.No
        Me.StartPosition = System.Windows.Forms.FormStartPosition.Manual
        Me.Text = "erwin Spy - erwin API Sample Client"
        Me.ERwinSpyMainMenu.ResumeLayout(False)
        Me.ERwinSpyMainMenu.PerformLayout()
        Me.SplitContainerObjectProperty.Panel1.ResumeLayout(False)
        Me.SplitContainerObjectProperty.Panel2.ResumeLayout(False)
        CType(Me.SplitContainerObjectProperty, System.ComponentModel.ISupportInitialize).EndInit()
        Me.SplitContainerObjectProperty.ResumeLayout(False)
        CType(Me.tvProperties, System.ComponentModel.ISupportInitialize).EndInit()
        Me.SplitContainerModelHelp.Panel1.ResumeLayout(False)
        Me.SplitContainerModelHelp.Panel2.ResumeLayout(False)
        CType(Me.SplitContainerModelHelp, System.ComponentModel.ISupportInitialize).EndInit()
        Me.SplitContainerModelHelp.ResumeLayout(False)
        Me.TableLayoutPanelMain.ResumeLayout(False)
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub
    Friend WithEvents Label1 As System.Windows.Forms.Label
    Friend tvObjectsToolTip As System.Windows.Forms.ToolTip
    Friend WithEvents mnFileOpen As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnClose As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnSep2 As System.Windows.Forms.ToolStripSeparator
    Friend WithEvents mnShowIds As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnSyncProp As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnIntelPropView As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnExtraDebug As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnOptions As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnSep1 As System.Windows.Forms.ToolStripSeparator
    Friend WithEvents mnExit As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnFile As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnIntrinsicMetamodel As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnMetaModelsArray As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnEM2ModelsArray As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnEM2IntrinsicMetamodel As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnEM2MetaModelsArray As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnModels As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnSpyHelp As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnAbout As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents mnHelp As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents ERwinSpyMainMenu As System.Windows.Forms.MenuStrip
    Friend WithEvents wbHelp As System.Windows.Forms.WebBrowser
    Friend WithEvents frmSplitH As System.Windows.Forms.GroupBox
    Friend ERwinSpyOpenFileDialog As System.Windows.Forms.OpenFileDialog
    Friend WithEvents LabelHelp As System.Windows.Forms.Label
    Friend WithEvents ERwinSpyLabel As System.Windows.Forms.Label
    Friend WithEvents LabelProperties As System.Windows.Forms.Label
    Friend WithEvents LabelObjects As System.Windows.Forms.Label
    Friend WithEvents SplitContainerObjectProperty As System.Windows.Forms.SplitContainer
    Friend WithEvents SplitContainerModelHelp As System.Windows.Forms.SplitContainer
    Friend WithEvents TableLayoutPanelMain As System.Windows.Forms.TableLayoutPanel
    Friend WithEvents tvObjects As System.Windows.Forms.TreeView
    Friend WithEvents tvProperties As System.Windows.Forms.DataGridView
    Friend WithEvents mnModelsArray As System.Windows.Forms.ToolStripMenuItem
    Friend WithEvents PropertyName As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyDataType As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyValue As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyValueAsString As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyNULL As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyUserDefined As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyVector As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyTool As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyReadOnly As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyDerived As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyFacetsTrue As System.Windows.Forms.DataGridViewTextBoxColumn
    Friend WithEvents PropertyFacetsFalse As System.Windows.Forms.DataGridViewTextBoxColumn
End Class
