# Ch18.InterfaceTests

The interface chapter's contract across its four projects, in one offline
harness (`RESULT n/n`): `Ch18.AguiServer` in process with its status stream
and review routes, the client's views, the approval desk.

| case | what it proves |
|---|---|
| replay | a client drops, the claim is approved meanwhile, the client resumes from its cursor: one new event, every event id applied once |
| stale binding | the arguments change under an open review screen: the old version is refused (`stale_version`, `digest_mismatch`), the new one commits its amount |
| double click | two approve POSTs at once (`Task.Run`): one decision, one replay, one commit, one Completed event |
| slow client | a stalled subscriber stays at its bound, is dropped, and gets a snapshot when it reconnects |
| revoked reviewer | the role is removed mid-review: review agent 403, screen 404, POST 403 |
| wrong identity | a valid thread id or proposal id under another identity or tenant: 404 (403 where the route's policy refuses first) |

```bash
dotnet run --project src/Ch18.StreamingInterfaces/Ch18.InterfaceTests -- --self-test
```

Fake model, TestServer, minted tokens. AG-UI packages are 1.5.0-preview.
