# From DB Temizlik Planı

## Amaç
"From DB" (Generate DDL -> DB karşılaştırması) yolu yanlış çalışıyor. Yeniden yazacağız. Önce eski/bozuk kodları temizle. Dialog (DbConnectionForm) ve UI (rbFromDB, btnConfigureDB) kalsın; _db* state alanları kalsın. BtnCaptureRE debug listener + DumpComMembers/DumpComTypeInfo/ProbeMethod + Win32Helper IAccessible bolumu **kalsin** (debug icin).

## Korunacaklar (DOKUNMA)
- [x] Forms/DbConnectionForm.cs
- [x] ModelConfigForm.cs: _dbConnectionString, _dbPassword, _dbLabel, _dbTargetServer, _dbTargetVersion
- [x] ModelConfigForm.cs: OnRightSourceChanged, BtnConfigureDB_Click
- [x] ModelConfigForm.Designer.cs: rbFromMart, rbFromDB, btnConfigureDB, cmbRightModel, btnCaptureRE
- [x] ModelConfigForm.cs: BtnCaptureRE_Click, DumpComMembers, DumpComTypeInfo, ProbeMethod, GetTypeInfoDelegate
- [x] Services/Win32Helper.cs: IAccessible region
- [x] DdlGenerationService.cs: GenerateDiffWithDuplicate + Mart version path
- [x] DdlHelper/Program.cs: ListVersions + GenerateDDL (action=versions, action=ddl)

## Silinecekler

### 1. ModelConfigForm.cs - BtnGenerateDDL_Click icindeki isFromDB dali
- [ ] Satir 2211-2226: isFromDB ve DB validation (short-circuit: From DB secili ise "not implemented, will be rewritten" mesaji ver, return)
- [ ] Satir 2246-2249: isFromDB wait message dali
- [ ] Satir 2264: isFromDB status dali
- [ ] Satir 2320-2347: if (isFromDB) Task.Run -> GenerateDiffWithDatabase blogu

### 2. Services/DdlGenerationService.cs - Broken DB diff methodlari
- [ ] GenerateDiffWithDatabase (sat 240-287)
- [ ] ForwardEngineerViaHelper (sat 289-382)
- [ ] GenerateDbDdlViaOdbc (sat 384-502)
- [ ] CreateBlankErwinModel (sat 504-543)
- [ ] LoadDbDdlViaDdlHelper (sat 545-639)

### 3. tools/DdlHelper/Program.cs - DB actions
- [ ] Args: connStr, dbPass, targetServer, targetVersion, tableFilter, schemaFilter parse
- [ ] action=probe / action=fedb / action=redb routing
- [ ] ForwardEngineerFromDB method (sat 258-413)
- [ ] ReverseEngineerFromDB method (sat 415-653)

### 4. tools/DdlHelper/ProbeTargetServer.cs
- [ ] Komple dosya sil

### 5. ErwinAddIn.csproj
- [ ] System.Data.Odbc PackageReference sil (GenerateDbDdlViaOdbc icin eklenmisti)

## Dogrulama
- [ ] dotnet build erwin-addin.sln basarili
- [ ] DdlHelper.csproj build basarili
- [ ] "From Mart" DDL diff akisi etkilenmemis
- [ ] "From DB" sectiginde Generate DDL -> temiz "not implemented" mesaji

## Sonrasi
Temizlik tamamlaninca "From DB" yolunun yeni tasarimini konusacagiz. DbConnectionForm cikislari hazir (_dbConnectionString, _dbPassword, _dbLabel, _dbTargetServer, _dbTargetVersion) - yeni implementasyon bunlari direkt kullanabilir.
