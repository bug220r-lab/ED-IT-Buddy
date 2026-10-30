"""Build a hash-guarded source overlay; no installed files are changed."""
from pathlib import Path
import hashlib,json,sys

ROOT=Path(__file__).resolve().parent
SOURCE=Path(r'C:\Users\Jude\Documents\Codex\INTERLINKED')
ORIGINAL_PROJECT='repository/work/magix-all-projects/candidate/magix_projects.py'
PROJECT='repository/work/magix-buddy/candidate/magix_projects.py'
FILES=('repository/app/interlinked_gui.py','repository/app/interlinked_controller.py',ORIGINAL_PROJECT,'repository/app/web/app.js','repository/app/web/index.html','repository/app/web/style.css')
def sha(data):return hashlib.sha256(data).hexdigest()
def replace(text,old,new):
    assert text.count(old)==1,('Patch anchor changed',old[:100]);return text.replace(old,new,1)
def main():
    candidate=ROOT/'candidate';candidate.mkdir(exist_ok=True)
    records=[];data={}
    for rel in FILES:
        raw=(SOURCE/rel).read_bytes();data[rel]=raw.decode('utf-8-sig')
        records.append(dict(path=PROJECT if rel==ORIGINAL_PROJECT else rel,before_sha256=None if rel==ORIGINAL_PROJECT else sha(raw),source_path=rel,source_sha256=sha(raw)))
    module=(ROOT/'interlinked_buddy.py').read_bytes();module_hash=sha(module)
    project=PROJECT
    text=data[ORIGINAL_PROJECT]
    text=replace(text,"PROTOCOL='magix-multiple-projects-v1'","BUDDY_SHA='"+module_hash+"'\nPROTOCOL='magix-multiple-projects-v1'")
    text=replace(text,'def open(self,project,entry,prompt=input,launch=None):','def open(self,project,entry,prompt=input,launch=None,buddy_session=None):')
    text=replace(text,'        return selected.session(entry,prompt=prompt,launch=launch_selected)',"""        if buddy_session:
            need(isinstance(buddy_session,str) and len(buddy_session)==32 and all(c in '0123456789abcdef' for c in buddy_session),'Invalid Buddy session identity.')
            module_path=self.c.kit/'INTERLINKED-App'/'interlinked_buddy.py'
            need(self.e.file_digest(module_path)==BUDDY_SHA,'Reviewed Buddy ingestion module changed.')
            spec=importlib.util.spec_from_file_location('interlinked_buddy_ingest',module_path)
            module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
            prompt=module.magix_prompt(self,selected,project,buddy_session,self.c.kit/'INTERLINKED-GUI-State'/'buddy',prompt)
        return selected.session(entry,prompt=prompt,launch=launch_selected)""")
    text=replace(text,"    parser.add_argument('--project');parser.add_argument('--entry');parser.add_argument('--confirm')","    parser.add_argument('--project');parser.add_argument('--entry');parser.add_argument('--confirm');parser.add_argument('--buddy-session')")
    text=replace(text,"result=manager.open(args.project,args.entry)","result=manager.open(args.project,args.entry,buddy_session=args.buddy_session)")
    data[project]=text
    gui='repository/app/interlinked_gui.py';text=data[gui]
    import re
    text,n=re.subn(r"MAGIX_SHA='[a-f0-9]{64}'","MAGIX_SHA='"+sha(data[project].encode('utf-8'))+"'",text,count=1);assert n==1
    text=replace(text,"'interlinked_master','interlinked_jobs')","'interlinked_master','interlinked_jobs','interlinked_buddy')")
    text=replace(text,"'interlinked_jobs.py','web/index.html'","'interlinked_jobs.py','interlinked_buddy.py','web/index.html'")
    text=replace(text,"seal.get('format')=='interlinked-gui-checksums-v4'","seal.get('format')=='interlinked-gui-checksums-v5'")
    text=replace(text,"    controller=load_companion('interlinked_controller').controller_class(sys.modules[__name__])(runtime);server=Server(controller,args.port)","    load_companion('interlinked_buddy').install(sys.modules[__name__])\n    controller=load_companion('interlinked_controller').controller_class(sys.modules[__name__])(runtime);server=Server(controller,args.port)")
    data[gui]=text
    control='repository/app/interlinked_controller.py';text=data[control]
    text=replace(text,'        def session_input(self,value,operation_id,prompt_generation):','        def session_input(self,value,operation_id,prompt_generation):\n            g.need(not getattr(getattr(self,\'buddy\',None),\'pending\',None),\'Wait for the current asset delivery to finish.\')')
    data[control]=text
    javascript='repository/app/web/app.js';text=data[javascript]
    text=replace(text,'if(record)recordRecent(selected.id).catch(showError);}','if(record)recordRecent(selected.id).catch(showError);if($(\'buddy-report-panel\'))buddyLoadReport();}')
    text=replace(text,' else if(Date.now()-lastCatalogRefresh>8000&&!mutation){await loadCatalog();}\n}'," else if(Date.now()-lastCatalogRefresh>8000&&!mutation){await loadCatalog();}\n if($('buddy-report-panel')&&!document.hidden&&Date.now()-buddyReportRefreshed>5000)await buddyLoadReport();\n}")
    data[javascript]=text+'\n'+(ROOT/'buddy_report_ui.js').read_text('utf-8')
    html='repository/app/web/index.html'
    data[html]=replace(data[html],'  <section class="activity">',(ROOT/'buddy_report_panel.html').read_text('utf-8')+'  <section class="activity">')
    css='repository/app/web/style.css';data[css]+='\n'+(ROOT/'buddy_report_style.css').read_text('utf-8')
    for row in records:
        out=candidate/row['path'];out.parent.mkdir(parents=True,exist_ok=True);raw=data[row['path']].encode('utf-8');out.write_bytes(raw);row['sha256']=sha(raw)
    target=candidate/'repository/app/interlinked_buddy.py';target.write_bytes(module)
    records.append(dict(path='repository/app/interlinked_buddy.py',before_sha256=None,sha256=module_hash))
    (ROOT/'source-overlay.json').write_text(json.dumps(dict(format='interlinked-buddy-source-overlay-v1',source=str(SOURCE),files=records,required_gui_seal=dict(format='interlinked-gui-checksums-v5',additional_files=['interlinked_buddy.py'],magix_sha256=sha(data[project].encode('utf-8')),frozen_base_unchanged=True),publication_deferred=True),indent=2)+'\n','utf-8')
    print(json.dumps(dict(candidate=str(candidate),files=len(records),module_sha256=module_hash)))
if __name__=='__main__':main()
