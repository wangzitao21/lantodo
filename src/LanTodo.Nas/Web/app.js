let currentSpaceRoot=null;
let token = '', loading = false, connected = false, authConfigured = false, currentNasId = "", currentNasName = "";
const $ = id => document.getElementById(id);
const message = value => { $('message').textContent = value; };
async function api(path, body) {
  const response = await fetch('/api/' + path, { method: body ? 'POST' : 'GET', headers: { Authorization: 'Bearer ' + token, ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined });
  if (response.status === 401) { showLogin(); throw Error('管理令牌无效，请重新登录。'); }
  const data = await response.json(); if (!response.ok) throw Error(data.error || '操作未完成'); return data;
}
function showLogin() { connected = false; token = ''; $('token').value = ''; $('inviteCode').value = ''; $('inviteResult').hidden = true; $('dashboard').hidden = true; $('login').hidden = false; $('logout').hidden = true; }
function action(label, callback, danger = false) { const b = document.createElement('button'); b.textContent = label; if (danger) b.className = 'danger'; b.onclick = async () => { b.disabled = true; try { await callback(); } catch (e) { message(e.message); } finally { b.disabled = false; } }; return b; }
function row(container, title, detail, button) { const r = document.createElement('div'); r.className = 'row'; const text = document.createElement('div'); const p = document.createElement('p'); p.textContent = title; const small = document.createElement('small'); small.textContent = detail; text.append(p, small); r.append(text); if (button) r.append(button); container.append(r); }
function empty(container, text) { if (!container.childElementCount) { const p = document.createElement('p'); p.className = 'empty'; p.textContent = text; container.append(p); } }
const date = value => value ? new Date(value).toLocaleString('zh-CN') : '暂无记录';
async function command(kind, extra = {}) { const result = await api('command', { kind, spaceRoot:currentSpaceRoot, ...extra }); await refresh(); return result.result; }
async function refresh() {
  if (loading) return; loading = true;
  try {
    const s = await api('status'); $('login').hidden = true; $('dashboard').hidden = false; $('logout').hidden = !authConfigured; connected = true;
    currentSpaceRoot=s.space?.id??null;
    currentNasId = s.deviceId; currentNasName = s.name; $('nasName').textContent = s.name; $('syncStatus').textContent = s.syncStatus; $('lastSync').textContent = '最近同步：' + date(s.lastSync) + (s.backupError ? ' · 备份异常：' + s.backupError : '');
    $('deviceCount').textContent = s.space?.active ? s.space.members : 0; $('pendingCount').textContent = s.invites.filter(i => i.state === '待绑定').length;
    const picker=$('spacePicker');
    if(document.activeElement!==picker) { picker.replaceChildren(); for(const space of s.spaces||[]) { const option=document.createElement('option');option.value=space.key;option.textContent=space.name+(space.member?'':' · 已退出');option.selected=space.selected;picker.append(option); } }
    $('spaceSummary').textContent = !s.space ? '旧版连接规则，等待升级' : s.space.active ? s.spaceName + ' · 本机是其中一位成员' : '已退出空间，本机数据仍保留';
    $('upgradePanel').hidden = !s.needsUpgrade; $('leaveSpace').hidden = !s.space?.active; $('newSpace').hidden = !s.space || s.space.active;
    $('addDevice').disabled = $('newInvite').disabled = !s.space?.active;
    if (!$('publicAddress').dataset.loaded) { $('publicAddress').value = s.advertisedAddress || (!['localhost','127.0.0.1','[::1]'].includes(location.hostname) ? location.hostname + ':' + s.port : ''); $('publicAddress').dataset.loaded = 'true'; }
    const devices = $('deviceList'); devices.replaceChildren();
    for (const d of s.devices) {
      const n = s.nodes.find(n => n.deviceId === d.id), buttons = document.createElement('div'); buttons.className = 'actions';
      buttons.append(action('改名', () => rename(d.id, d.name)), action('移除', async () => { const spaceRoot=currentSpaceRoot;if (await confirmAction('将“' + d.name + '”从整个空间移除？其他成员会同步此决定，对方本机已有数据仍保留。重新加入需要新授权码。')) { await command('revoke', { ids: [d.id],spaceRoot }); message('已移除成员，离线设备连接后会收到此决定'); } }, true));
      const invite = s.invites.find(i => i.deviceId === d.id);
      row(devices, d.name, (n ? n.state : d.state) + (invite ? ' · 通过本机邀请加入' : ' · 空间成员') + ' · ' + d.id.slice(0, 8), buttons);
    }
    empty(devices, '目前没有其他成员。点击“添加设备”，或加入另一台设备的空间。');
    const routes = $('routeList'); routes.replaceChildren();
    for (const d of s.devices) {
      const n = s.nodes.find(n => n.deviceId === d.id);
      row(routes,d.name,(n?.address || '自动寻找地址') + (n?.error ? ' · ' + n.error : ''),action('设置备用地址', async () => { const spaceRoot=currentSpaceRoot;const address = await ask('此设备可达的 IP / 域名:同步端口',n?.address || ''); if(address !== null) { await command(address.trim() ? 'address' : 'remove-address',{ids:[d.id],name:address.trim(),spaceRoot}); message('备用地址已更新'); } }));
    }
    const invites = $('inviteList'); invites.replaceChildren();
    for (const i of [...s.invites].reverse()) row(invites, i.state + (i.deviceId ? ' → ' + ([...s.devices, ...s.revokedDevices].find(d => d.id === i.deviceId)?.name || i.deviceId.slice(0,8)) : ''), '生成 ' + date(i.created) + ' · 到期 ' + date(i.expires), i.state === '待绑定' ? null : action('删除记录', async () => { await command('delete-invite', { ids: [i.id] }); message('记录已删除，设备绑定状态不受影响'); }, true));
    empty(invites, '尚未添加设备。');
    renderConflicts(s.conflicts || []);
    if (!s.invites.some(i => i.state === '待绑定')) { $('inviteCode').value = ''; $('inviteResult').hidden = true; }
  } finally { loading = false; }
}
function bind(id, run) { $(id).onclick = async () => { $(id).disabled = true; try { await run(); } catch (e) { message(e.message); } finally { $(id).disabled = false; } }; }
$('loginForm').onsubmit = async e => { e.preventDefault(); token = $('token').value.trim(); try { await api('login', {}); token = ''; await initialize(); $('token').value = ''; message('已连接 NAS'); } catch (e) { message(e.message); } };
$('logout').onclick = async () => { try { await api('logout', {}); showLogin(); message('已退出登录'); } catch (e) { message(e.message); } };
bind('refresh', async () => { await refresh(); message('状态已更新'); });
bind('sync', async () => { await command('sync'); message('已请求同步'); });
bind('backup', async () => message('备份已保存到 NAS：' + await command('backup')));
bind('newInvite', async () => { const result = await api('command',{kind:'invite',spaceRoot:currentSpaceRoot,name:$('publicAddress').value.trim()}); await refresh(); $('inviteCode').value = result.result; $('inviteQr').src = 'data:image/png;base64,' + result.qr; $('inviteResult').hidden = false; message('授权码与二维码已生成，5 分钟内有效'); });
bind('cancelInvite', async () => { await command('cancel-invite'); $('inviteCode').value = ''; $('inviteResult').hidden = true; message('当前授权码已作废'); });
bind('copyInvite', async () => { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText($('inviteCode').value); message('已复制'); } else { $('inviteCode').focus(); $('inviteCode').select(); message('已选中，请复制选中的授权码'); } });
$('pairForm').onsubmit = async e => { e.preventDefault(); if (!$('mergeConsent').checked) return; const b = e.target.querySelector('button'); b.disabled = true; try { await command('pair', { name: $('address').value.trim(), secret: $('pairCode').value.trim() }); $('pairCode').value = ''; $('mergeConsent').checked = false; $('joinDetails').open = false; message('已加入空间，成员和数据正在自动同步'); } catch (e) { message(e.message); } finally { b.disabled = false; } };
setInterval(() => { if (connected && !document.hidden) refresh().catch(e => message(e.message)); }, 10000);

async function initialize() {
  const auth = await api('auth'); authConfigured = auth.configured;
  $('securityHint').textContent = auth.managedByEnvironment ? '令牌由容器环境变量管理。' : auth.configured ? '已设置令牌。此浏览器会保持登录，更换令牌将使其他会话退出。' : '首次使用可直接进入。请在这里设置管理令牌，保护控制台。';
  $('securityForm').hidden = auth.managedByEnvironment;
  if (auth.authenticated) { await refresh(); $('logout').hidden = !auth.configured; } else showLogin();
}
$('generateToken').onclick = () => { const bytes = crypto.getRandomValues(new Uint8Array(24)); $('newToken').value = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join(''); $('newToken').type = 'text'; };
$('securityForm').onsubmit = async e => { e.preventDefault(); const b = e.target.querySelector('button.primary'); b.disabled = true; try { await api('security', { secret: $('newToken').value }); $('newToken').value = ''; $('newToken').type = 'password'; await initialize(); message('令牌已保存，当前浏览器已保持登录'); } catch (e) { message(e.message); } finally { b.disabled = false; } };
initialize().catch(e => message(e.message));

async function rename(id, oldName, spaceRoot=currentSpaceRoot) {
  const name = await ask('设备昵称（1–100 字，会同步到终端）', oldName);
  if (name === null) return;
  await command('rename', {ids:[id], name:name.trim(),spaceRoot}); message('昵称已保存，设备连接后自动更新');
}
bind('renameNas', () => rename(currentNasId,currentNasName));
bind('addDevice', async () => { $('invites').hidden=false; $('invites').scrollIntoView({block:'start'}); });
bind('closeInvite', async () => { $('invites').hidden=true; });
bind('savePublicAddress',async()=>{await command('public-address',{name:$('publicAddress').value.trim()});message('本机地址已保存，请重新生成邀请');});
bind('upgradeSpace',async()=>{const spaceRoot=currentSpaceRoot;if(await confirmAction('创建统一空间并替换旧版逐台配对？原清单与历史会保留，其他设备需用新授权码重新加入。')){await command('upgrade-space',{spaceRoot});message('统一空间已创建，请邀请其他设备加入');}});
bind('leaveSpace',async()=>{const spaceRoot=currentSpaceRoot;if(await confirmAction('退出当前连接空间并停止同步？本机清单和历史仍保留，其他成员会收到退出记录。')){await command('leave-space',{spaceRoot});message('已退出空间，本机数据已保留');}});
bind('newSpace',async()=>{await command('new-space');message('新空间已创建，可以邀请其他设备加入');});
function renderConflicts(todos) {
  const list=$('conflictList'); list.replaceChildren();
  for (const todo of todos) {
    const group=document.createElement('div'); group.className='conflict';
    const versions=document.createElement('div'); versions.className='versions';
    for(const head of todo.heads) {
      const card=document.createElement('article');card.className='version';
      const source=document.createElement('small');source.textContent=head.name+' · '+date(head.createdUtc);
      const title=document.createElement('h3');title.textContent=(head.data.deleted?'已删除 · ':head.data.completed?'已完成 · ':'待办 · ')+head.data.title;
      const content=document.createElement('p');content.textContent=[head.data.date,head.data.time,head.data.notes,...(head.data.attachments||[]).map(a=>a.name)].filter(Boolean).join('\n');
      card.append(source,title,content,action(head.data.deleted?'采用删除':'保留这个版本',async()=>{await command('resolve',{ids:[todo.id,head.id,...todo.versions]});message('已确认，将自动同步');}));versions.append(card);
    }
    group.append(versions,action('删除此条',async()=>{await command('resolve',{ids:[todo.id,todo.heads[0].id,...todo.versions],name:'delete'});message('已删除，历史仍保留');},true));list.append(group);
  }
  empty(list,'✓ 没有需要确认的内容');
}

bind('renameSpace',async()=>{const spaceRoot=currentSpaceRoot;const value=await ask('空间名称（会同步给空间成员）',$('spacePicker').selectedOptions[0]?.textContent||'');if(value!==null)await command('rename-space',{name:value,spaceRoot});});
bind('createSpace',async()=>{const value=await ask('新空间名称，例如：家里、公司');if(value!==null)await command('new-space',{name:value});});
bind('joinSpace',async()=>{$('joinDetails').open=true;$('joinDetails').scrollIntoView({block:'center'});$('pairCode').focus();});
$('spacePicker').onchange=async()=>{try{$('publicAddress').dataset.loaded='';$('invites').hidden=true;await command('select-space',{name:$('spacePicker').value});}catch(e){message(e.message);}};

function ask(label,value='') {
  return new Promise(resolve=>{
    const dialog=document.createElement('dialog');dialog.className='input-dialog';
    const form=document.createElement('form');form.method='dialog';
    const heading=document.createElement('h2');heading.textContent=label;
    const input=document.createElement('input');input.value=value;input.maxLength=300;input.setAttribute('aria-label',label);
    const actions=document.createElement('div');actions.className='actions';
    const cancel=document.createElement('button');cancel.type='button';cancel.textContent='取消';
    const save=document.createElement('button');save.className='primary';save.textContent='保存';
    let finished=false;const finish=value=>{if(finished)return;finished=true;dialog.close();dialog.remove();resolve(value);};
    cancel.onclick=()=>finish(null);dialog.oncancel=e=>{e.preventDefault();finish(null);};form.onsubmit=e=>{e.preventDefault();finish(input.value.trim());};
    actions.append(cancel,save);form.append(heading,input,actions);dialog.append(form);document.body.append(dialog);dialog.showModal();input.focus();input.select();
  });
}

async function confirmAction(message,title='确认操作',positive='确认继续') {
  return new Promise(resolve=>{
    const dialog=document.createElement('dialog');dialog.className='input-dialog';
    const heading=document.createElement('h2');heading.textContent=title;
    const detail=document.createElement('p');detail.className='muted';detail.textContent=message;
    const actions=document.createElement('div');actions.className='actions';
    const cancel=document.createElement('button');cancel.textContent='取消';
    const accept=document.createElement('button');accept.textContent=positive;accept.className='danger';
    const previous=document.activeElement;let finished=false;
    const finish=result=>{if(finished)return;finished=true;dialog.close();dialog.remove();previous?.focus();resolve(result);};
    cancel.onclick=()=>finish(false);accept.onclick=()=>finish(true);dialog.oncancel=e=>{e.preventDefault();finish(false);};
    actions.append(cancel,accept);dialog.append(heading,detail,actions);document.body.append(dialog);dialog.showModal();cancel.focus();
  });
}
bind('deleteSpace',async()=>{
  const spaceRoot=currentSpaceRoot;const name=$('spacePicker').selectedOptions[0]?.textContent||'当前连接空间';
  if(await confirmAction('退出并从本机移除这组网络连接。全部清单、历史和附件会保留，其他连接空间继续同步。重新加入需要新的授权码。','删除“'+name+'”？','删除空间')) {
    await command('delete-space',{spaceRoot});$('publicAddress').dataset.loaded='';$('invites').hidden=true;message('已移除网络连接，全部内容保留');
  }
});
