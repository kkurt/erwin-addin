Friend Class frmAbout
    Inherits System.Windows.Forms.Form
    Private oApplication As SCAPI.Application ' SCAPI Application

    ' Init the form with SCAPI Application reference
    Sub Init(ByRef oApp As SCAPI.Application)
        oApplication = oApp
    End Sub

    Private Sub cmdSysInfo_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles cmdSysInfo.Click
        Try
            Process.Start("msinfo32.exe")
        Catch
            MsgBox("System Information Is Unavailable At This Time", MsgBoxStyle.OkOnly)
        End Try
    End Sub

    Private Sub cmdOK_Click(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles cmdOK.Click
        Me.Close()
    End Sub

    Private Sub frmAbout_Load(ByVal eventSender As System.Object, ByVal eventArgs As System.EventArgs) Handles MyBase.Load
        Me.Text = "About " & My.Application.Info.Title
        lblVersion.Text = "Version 15.0 / Sample Application. No support provided"
        lblTitle.Text = My.Application.Info.Title

        Dim buildNumber = Mid(oApplication.Version, 10)
        lblDescription.Text = "erwin Data Modeler 15.0." & buildNumber & Chr(10) & "SCAPI Version 9.0 (15.0." & buildNumber & ")"
    End Sub
End Class