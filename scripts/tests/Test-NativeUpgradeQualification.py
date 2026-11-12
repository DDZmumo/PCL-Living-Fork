#!/usr/bin/env python3
"""Regress actual native-upgrade YAML gates with non-executable offline receipts.

Requires PyYAML==6.0.2 and PowerShell 7 (pwsh). All fixtures/outputs stay in one
TemporaryDirectory. No product, bridge, build, network, profile or Registry entry.
Windows cold/upgrade acceptance remains the separate real workflow's job.
"""
from pathlib import Path
import argparse
import os
import hashlib
import json
import zipfile
import shutil
import subprocess
import tempfile
import yaml


def main():
    repository=Path(__file__).resolve().parents[2]
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workflow',type=Path,default=repository/'.github/workflows/native-main.yml')
    parser.add_argument('--powershell',default='pwsh')
    parser.add_argument('--allow-missing-symlink-privilege', action='store_true',
                        help='Local Windows diagnostics only: report the symlink case PENDING on WinError 1314; CI remains strict by default')
    args=parser.parse_args()
    workflow=args.workflow.resolve()
    assert workflow.is_file(),'Actual Native Main workflow is missing'
    ps=shutil.which(args.powershell) or str(Path(args.powershell).resolve())
    assert Path(ps).is_file(),'PowerShell 7 pwsh is required (use --powershell)'
    def execute(script,environment):
        return subprocess.run([ps,'-NoLogo','-NoProfile','-NonInteractive','-File',str(script)],env=environment,
                              capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=30)
    with tempfile.TemporaryDirectory(prefix='pcllf-native-upgrade-gates-') as temporary:
        r=Path(temporary)
        w=yaml.safe_load(workflow.read_text(encoding='utf-8'))
        legacy_block=next(step['run'] for step in w['jobs']['native-upgrade']['steps'] if step['name']=='Verify the fixed archived legacy product fixture')
        legacy_script=r/'archive.ps1';legacy_script.write_text(legacy_block,encoding='utf-8')
        for case in ('valid','missing','tampered'):
            workspace=r/('archive-'+case);workspace.mkdir()
            (workspace/'artifacts/native-upgrade').mkdir(parents=True)
            source=workspace/'tests/fixtures/legacy-upgrade/lf-8ff80de.zip';source.parent.mkdir(parents=True)
            if case!='missing':
                shutil.copyfile(repository/'tests/fixtures/legacy-upgrade/lf-8ff80de.zip',source)
                if case=='tampered':
                    with source.open('r+b') as handle:
                        original=handle.read(1);handle.seek(0);handle.write(bytes([original[0]^1]))
            exports=workspace/'github-env';exports.write_text('')
            archive_env={key:os.environ[key] for key in ('PATH','HOME','TMPDIR','TMP','TEMP','SYSTEMROOT','WINDIR','PSModulePath','LANG','LC_ALL') if key in os.environ}
            archive_env.update(GITHUB_WORKSPACE=str(workspace),GITHUB_ENV=str(exports))
            checked=execute(legacy_script,archive_env)
            assert (checked.returncode==0)==(case=='valid'),case+checked.stdout+checked.stderr
            if case=='valid':
                provenance=json.loads((workspace/'artifacts/native-upgrade/legacy-provenance.json').read_text(encoding='utf-8-sig'))
                assert provenance['archiveSource']=='repository:tests/fixtures/legacy-upgrade/lf-8ff80de.zip' and provenance['rawArchiveVerified'] is True
                assert 'UPGRADE_LEGACY_EXE=' in exports.read_text(encoding='utf-8-sig')
            else:
                assert not exports.read_bytes() and not (workspace/'artifacts/native-upgrade/legacy-provenance.json').exists()
            print('PASS actual archived legacy input '+case+': fixed byte checks only; no executable launched')
        producer=next(x['run'] for x in w['jobs']['native']['steps'] if x.get('id')=='upgrade_qualification')
        consumer=next(x['run'] for x in w['jobs']['native-upgrade']['steps'] if x.get('id')=='upgrade_inputs')
        (r/'producer.ps1').write_text(producer,encoding='utf-8');(r/'consumer.ps1').write_text(consumer,encoding='utf-8')
        work=r/'offline';work.mkdir(exist_ok=True)
        base=work/'producer'
        if base.exists():shutil.rmtree(base)
        p=base/'package';p.mkdir(parents=True,exist_ok=True)
        sha=lambda f:hashlib.sha256(f.read_bytes()).hexdigest()
        write=lambda f,v:f.write_text(json.dumps(v,indent=2)+'\n')
        (p/'LivingPCL.exe').write_bytes(b'OFFLINE QUALIFICATION ROUTING FIXTURE ONLY; THIS IS NOT AN EXECUTABLE')
        (p/'PCL-UpdateBridge.exe').write_bytes(b'OFFLINE NONEXECUTABLE MANAGED BRIDGE PLACEHOLDER; NEVER RUN')
        with zipfile.ZipFile(p/'LivingPCL.zip','w') as z:z.write(p/'LivingPCL.exe','LivingPCL.exe')
        (p/'SHA256SUMS.txt').write_text(''.join(f'{sha(p/n)}  {n}\n' for n in ['LivingPCL.exe','LivingPCL.zip','PCL-UpdateBridge.exe']))
        roles=[]
        for role,name in [('native-exe','LivingPCL.exe'),('native-zip','LivingPCL.zip'),('legacy-bridge','PCL-UpdateBridge.exe'),('checksums','SHA256SUMS.txt')]:
         a={'role':role,'fileName':name,'bytes':(p/name).stat().st_size,'sha256':sha(p/name),'path':'DO-NOT-DEREFERENCE-PRODUCER-ABSOLUTE-PATH'}
         if role=='native-zip':a.update(nativeEntryName='LivingPCL.exe',nativeEntrySha256=sha(p/'LivingPCL.exe'))
         roles.append(a)
        index={'schemaVersion':1,'kind':'PCLLF.NativeRelease.Artifacts','configuration':'Release','runtimeIdentifier':'win-x64','bridgeOnly':False,'nativePayloadSha256':sha(p/'LivingPCL.exe'),'productVersion':'99.0.0','artifacts':roles}
        write(p/'NativeRelease.Artifacts.json',index)
        coldDir=base/'smoke'/('NativeSmoke-'+'a'*32);coldDir.mkdir(parents=True,exist_ok=True)
        cold={'Result':'passed','ForcedCleanup':False,'CleanupErrors':[],'MainProof':{'sha256':sha(p/'LivingPCL.exe'),'buildType':0},'Runs':[{'Pass':i,'NormalExit':True,'ExitCode':-1,'ExpectedInformationNoticeVisible':True,'Window':{'Title':'LivingPCL','Class':'HwndWrapper[OFFLINE]','Left':0,'Top':0,'Right':100,'Bottom':100}} for i in [1,2]]}
        write(coldDir/'evidence.json',cold)
        identityDir=base/'bridge-identity'/('BridgeIdentity-'+'b'*32);identityDir.mkdir(parents=True,exist_ok=True)
        identity={'count':11,'results':[{'name':'genuine-native-main','accepted':True,'nativeInspectorUsed':True,'nativeIdentity':{'AssemblyName':'LivingPCL','Product':'PCL Living Fork'}},{'name':'genuine-managed-original-rule','accepted':True,'nativeInspectorUsed':False}]+[{'name':n,'accepted':False} for n in ['foreign-managed-identity','unbundled-native-apphost','invalid-PE','missing-file-IO','directory-access-error','exclusive-handle-IO','generic-invalid-operation','IO-never-native','permission-never-native']]}
        write(identityDir/'results.json',identity)
        env={key:os.environ[key] for key in ('PATH','HOME','TMPDIR','TMP','TEMP','SYSTEMROOT','WINDIR','PSModulePath','LANG','LC_ALL') if key in os.environ};env.update(GITHUB_REPOSITORY='DDZmumo/PCL-Living-Fork',GITHUB_SHA='c'*40,GITHUB_RUN_ID='123456789',GITHUB_RUN_ATTEMPT='2',NATIVE_LOG_DIRECTORY=str(base))
        result=execute(r/'producer.ps1',env)
        assert result.returncode==0,result.stdout+result.stderr
        print('PASS actual producer run block writes a bounded same-run manifest from synthetic offline receipts')
        q=json.loads((base/'native-upgrade-qualification.json').read_text());assert len(q['roles'])==4 and set(q['requiredSteps'])=={'main','resources','package','package_roles','credentials','metadata_identity','cold_entry'}
        cases=[]
        def run(name,change=None,expected=True,regenerate=False):
         folder=work/'cases'/name
         if folder.exists():shutil.rmtree(folder)
         destination=folder/'inputs/native-upgrade/current-release';destination.parent.mkdir(parents=True,exist_ok=True);shutil.copytree(base,destination)
         (folder/'artifacts/native-upgrade').mkdir(parents=True)
         e=env.copy();e.update(GITHUB_WORKSPACE=str(folder),GITHUB_ENV=str(folder/'github-env'))
         if change:change(destination,e)
         if regenerate:
          (destination/'native-upgrade-qualification.json').unlink()
          producer_environment=e|{'NATIVE_LOG_DIRECTORY':str(destination)}
          produced=execute(r/'producer.ps1',producer_environment)
          assert produced.returncode==0,produced.stdout+produced.stderr
         result=execute(r/'consumer.ps1',e)
         got=result.returncode==0
         if got!=expected:raise AssertionError(name+'\n'+result.stdout+result.stderr)
         if expected:
          actual=json.loads((folder/'artifacts/native-upgrade/relocated-inputs.json').read_text(encoding='utf-8-sig'))
          assert actual['sourceRunAttempt']=='2' and actual['ignoredProducerAbsolutePaths'] and len(actual['qualificationSha256'])==64
          assert len((folder/'github-env').read_text().splitlines())==2
         else:
          assert not (folder/'artifacts/native-upgrade/relocated-inputs.json').exists()
          assert not (folder/'github-env').exists()
         cases.append({'case':name,'expected':'qualified' if expected else 'reject before any bridge/input exports','observedExit':result.returncode})
         print('PASS '+name+': '+('qualified' if expected else 'rejected before bridge inputs'))
        def editQ(mutator):
         def edit(d,e):
          f=d/'native-upgrade-qualification.json';obj=json.loads(f.read_text());mutator(obj);write(f,obj)
         return edit
        def editReceipt(key,mutator):
         def edit(d,e):
          qf=d/'native-upgrade-qualification.json';q=json.loads(qf.read_text());f=d/q[key]['relativePath'];obj=json.loads(f.read_text());mutator(obj);write(f,obj);q[key]['sha256']=sha(f);q[key]['bytes']=f.stat().st_size;write(qf,q)
         return edit
        run('valid-same-run-relocated-package')
        run('cold-title-plain',editReceipt('coldEntry',lambda c:[run['Window'].__setitem__('Title','LivingPCL') for run in c['Runs']]),True,True)
        run('cold-title-full-width-space',editReceipt('coldEntry',lambda c:[run['Window'].__setitem__('Title','LivingPCL\u3000') for run in c['Runs']]),True,True)
        run('cold-title-foreign',editReceipt('coldEntry',lambda c:[run['Window'].__setitem__('Title','ForeignLauncher') for run in c['Runs']]),False)
        run('cold-title-suffix',editReceipt('coldEntry',lambda c:[run['Window'].__setitem__('Title','LivingPCL extra') for run in c['Runs']]),False)
        run('qualification-missing',lambda d,e:(d/'native-upgrade-qualification.json').unlink(),False)
        for key,value in [('repository','different/repo'),('sourceSha','d'*40),('sourceRunId','123456780'),('sourceRunAttempt','1'),('configuration','Debug'),('runtimeIdentifier','linux-x64')]:run('wrong-'+key,editQ(lambda q,k=key,v=value:q.__setitem__(k,v)),False)
        run('cold-gate-failed',editQ(lambda q:q['requiredSteps'].__setitem__('cold_entry','failure')),False)
        run('credential-gate-failed',editQ(lambda q:q['requiredSteps'].__setitem__('credentials','failure')),False)
        run('resource-gate-absent',editQ(lambda q:q['requiredSteps'].pop('resources')),False)
        run('unknown-extra-gate',editQ(lambda q:q['requiredSteps'].__setitem__('data','success')),False)
        run('evidence-path-escape',editQ(lambda q:q['coldEntry'].__setitem__('relativePath','../outside/evidence.json')),False)
        run('evidence-sha-tamper',lambda d,e:(d/'smoke'/('NativeSmoke-'+'a'*32)/'evidence.json').write_text('{}'),False)
        run('cold-pending',editReceipt('coldEntry',lambda c:c.__setitem__('Result','pending')),False)
        run('cold-forced-cleanup',editReceipt('coldEntry',lambda c:c.__setitem__('ForcedCleanup',True)),False)
        run('cold-cleanup-error',editReceipt('coldEntry',lambda c:c.__setitem__('CleanupErrors',['owned process still alive'])),False)
        run('cold-other-payload',editReceipt('coldEntry',lambda c:c['MainProof'].__setitem__('sha256','0'*64)),False)
        run('cold-debug-build',editReceipt('coldEntry',lambda c:c['MainProof'].__setitem__('buildType',100)),False)
        run('cold-incomplete-reopen',editReceipt('coldEntry',lambda c:c['Runs'].pop()),False)
        run('cold-close-not-normal',editReceipt('coldEntry',lambda c:c['Runs'][1].__setitem__('NormalExit',False)),False)
        run('cold-wrong-lf-exit',editReceipt('coldEntry',lambda c:c['Runs'][1].__setitem__('ExitCode',0)),False)
        run('cold-no-visible-window',editReceipt('coldEntry',lambda c:c['Runs'][1]['Window'].__setitem__('Right',0)),False)
        run('identity-native-rejected',editReceipt('metadataIdentity',lambda c:c['results'][0].__setitem__('accepted',False)),False)
        run('identity-foreign-product',editReceipt('metadataIdentity',lambda c:c['results'][0]['nativeIdentity'].__setitem__('Product','Foreign Product')),False)
        run('identity-managed-routed-native',editReceipt('metadataIdentity',lambda c:c['results'][1].__setitem__('nativeInspectorUsed',True)),False)
        run('package-index-tampered',lambda d,e:(d/'package/NativeRelease.Artifacts.json').write_text('{}'),False)
        run('qualified-role-sha-mismatch',editQ(lambda q:q['roles'][0].__setitem__('sha256','1'*64)),False)
        run('qualified-role-alias',editQ(lambda q:q['roles'][1].__setitem__('role',q['roles'][0]['role'])),False)
        run('package-payload-tampered',lambda d,e:(d/'package/LivingPCL.exe').write_bytes(b'changed non executable'),False)
        def symlinkReceipt(d,e):
         f=d/'smoke'/('NativeSmoke-'+'a'*32)/'evidence.json';target=d/'safe-copy.json';shutil.copy2(f,target);f.unlink();f.symlink_to(target)
        pending=[]
        try:
            run('evidence-reparse-symlink',symlinkReceipt,False)
        except OSError as error:
            if not args.allow_missing_symlink_privilege or os.name!='nt' or getattr(error,'winerror',None)!=1314:
                raise
            pending.append('evidence-reparse-symlink')
            print('PENDING evidence-reparse-symlink: local Windows token lacks symbolic-link privilege; default CI does not defer this case')
        # Static GitHub dependency/condition contract, not an Actions runner simulation.
        condition=w['jobs']['native-upgrade']['if'];assert "!cancelled()" in condition and "needs.native.result == 'failure'" in condition and "needs.native.result == 'success'" in condition
        assert 'continue-on-error' not in w['jobs']['native'] and not any(s.get('continue-on-error') for s in w['jobs']['native']['steps'])
        upload=next(s for s in w['jobs']['native']['steps'] if s.get('with',{}).get('name')=='native-main-${{ matrix.configuration }}-${{ github.run_attempt }}');assert upload['if'] in ('${{ always() }}', 'always()')
        qualification=next(s for s in w['jobs']['native']['steps'] if s.get('id')=='upgrade_qualification');assert all('steps.'+n+".outcome == 'success'" in qualification['if'] for n in q['requiredSteps'])
        assert w['jobs']['native']['steps'].index(qualification)<next(i for i,s in enumerate(w['jobs']['native']['steps']) if s['name'].startswith('Verify real Registry'))
        consumerIndex=next(i for i,s in enumerate(w['jobs']['native-upgrade']['steps']) if s.get('id')=='upgrade_inputs');legacyIndex=next(i for i,s in enumerate(w['jobs']['native-upgrade']['steps']) if s['name']=='Verify the fixed archived legacy product fixture');assert consumerIndex<legacyIndex
        producer_name=upload['with']['name'].replace('${{ matrix.configuration }}','Release')
        consumer_name=next(s for s in w['jobs']['native-upgrade']['steps'] if s.get('uses','').startswith('actions/download-artifact@'))['with']['name']
        assert producer_name==consumer_name
        first=producer_name.replace('${{ github.run_attempt }}','1');second=producer_name.replace('${{ github.run_attempt }}','2')
        assert first!=second and first=='native-main-Release-1' and second=='native-main-Release-2'
        print('PASS current-attempt artifact names prevent stale same-run rerun downloads')
        print('PASS workflow status, seven actual gates, always upload and fail-closed-before-old-download order')
        summary={'scope':'Actual workflow producer/consumer blocks and explicitly NON-EXECUTABLE offline fixtures only; no Windows product/bridge acceptance claimed','cases':cases,'counts':{'qualified':sum(c['expected']=='qualified' for c in cases),'rejected':sum(c['expected']!='qualified' for c in cases)}}
        summary['pending']=pending
        assert summary['counts']=={'qualified':3,'rejected':32-len(pending)},summary
        assert w['jobs']['native']['strategy']['fail-fast'] is False
        upgrade=w['jobs']['native-upgrade']['steps']
        assert not w['jobs']['native-upgrade'].get('continue-on-error') and not upgrade[consumerIndex].get('continue-on-error')
        download=next(step for step in upgrade if step.get('uses','').startswith('actions/download-artifact@'))
        assert download['with']['name']=='native-main-Release-${{ github.run_attempt }}' and download['with']['run-id']=='${{ github.run_id }}'
        legacy=upgrade[legacyIndex]
        assert 'tests/fixtures/legacy-upgrade/lf-8ff80de.zip' in legacy['run']
        assert '/actions/artifacts/' not in legacy['run'] and 'metadata.expired' not in legacy['run']
        for fixed in ('c0583c65e294e20c2c781ea7842f817ac8c57c84cb71cbb7d99a171392883bc3','f4c88a842d506c62e812d50493126c5f25a73b0bfa866bb749c0473e2740d151','5807581','10266112'):
            assert fixed in legacy['run']
        entry=next(step for step in upgrade if step['name']=='Require genuine old bridge and actual Native entry protocol cases')
        assert consumerIndex<legacyIndex<upgrade.index(entry) and not legacy.get('if') and not entry.get('if')
        assert "matrix.configuration == 'Release'" in qualification['if']
        print('PASS offline native-upgrade qualification: '+json.dumps(summary,ensure_ascii=False))


if __name__=='__main__':
    main()
